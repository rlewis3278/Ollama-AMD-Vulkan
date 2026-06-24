using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;

namespace OllamaToolkit.AiAssist;

public sealed class ModelComparisonService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private ModelComparisonCacheDocument? _cache;

    public ModelComparisonService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task<string> CompareAsync(
        string modelA,
        string modelB,
        ModelProfileSummary? summaryA,
        ModelProfileSummary? summaryB,
        string? categoryA = null,
        string? categoryB = null,
        CancellationToken cancellationToken = default)
    {
        var key = PairKey(modelA, modelB);
        var digest = ProfileDigest(summaryA) + "|" + ProfileDigest(summaryB);
        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (doc.Pairs.TryGetValue(key, out var cached)
            && string.Equals(cached.ProfileDigest, digest, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(cached.Comparison))
        {
            return cached.Comparison;
        }

        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.ModelComparison, cancellationToken)
                .ConfigureAwait(false))
        {
            return FallbackCompare(modelA, modelB, summaryA, summaryB);
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return FallbackCompare(modelA, modelB, summaryA, summaryB);
        }

        var benchA = FormatBench(summaryA);
        var benchB = FormatBench(summaryB);
        var prompt = $"""
            Compare these two Ollama models for an AMD Vulkan Windows laptop user in 3-5 sentences.
            A: {modelA} | {categoryA ?? "unknown"} | {benchA}
            B: {modelB} | {categoryB ?? "unknown"} | {benchB}
            Comparison:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 192, 4096, cancellationToken)
                .ConfigureAwait(false);
            var comparison = text.Trim();
            doc.Pairs[key] = new ModelComparisonCacheEntry
            {
                ModelA = modelA,
                ModelB = modelB,
                Comparison = comparison,
                ProfileDigest = digest,
                SummaryModel = summarizer,
                GeneratedAt = DateTimeOffset.Now.ToString("o")
            };
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
            return comparison;
        }
        catch
        {
            return FallbackCompare(modelA, modelB, summaryA, summaryB);
        }
    }

    private static string FallbackCompare(
        string modelA,
        string modelB,
        ModelProfileSummary? summaryA,
        ModelProfileSummary? summaryB)
    {
        var a = summaryA?.NeedsRetest == false
            ? $"{modelA} best on {summaryA.BestMode} at {summaryA.BestTps:F1} tok/s."
            : $"{modelA} is not benchmarked yet.";
        var b = summaryB?.NeedsRetest == false
            ? $"{modelB} best on {summaryB.BestMode} at {summaryB.BestTps:F1} tok/s."
            : $"{modelB} is not benchmarked yet.";
        return $"{a} {b}";
    }

    private static string FormatBench(ModelProfileSummary? s) =>
        s is null || s.NeedsRetest
            ? "untested"
            : $"{s.SizeGB} GB, {s.BestMode} {s.BestTps:F1} tok/s";

    private static string ProfileDigest(ModelProfileSummary? s) =>
        s is null ? "none" : $"{s.Model}:{s.BestMode}:{s.BestTps:F1}:{s.LastTested}";

    private static string PairKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}|{b}" : $"{b}|{a}";

    private async Task<ModelComparisonCacheDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<ModelComparisonCacheDocument>(
            ConfigPaths.ModelComparisonCacheFile, cancellationToken).ConfigureAwait(false)
            ?? new ModelComparisonCacheDocument();
        _cache.Pairs ??= new Dictionary<string, ModelComparisonCacheEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private async Task SaveAsync(ModelComparisonCacheDocument doc, CancellationToken cancellationToken)
    {
        doc.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = doc;
        await JsonFileHelper.WriteAsync(ConfigPaths.ModelComparisonCacheFile, doc, cancellationToken)
            .ConfigureAwait(false);
    }
}