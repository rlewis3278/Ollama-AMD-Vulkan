using System.Text.Json;
using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCategory;

namespace OllamaToolkit.AiAssist;

public sealed class BenchmarkSettingsAdvisorService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private BenchmarkSettingsDocument? _cache;

    public void ClearCache() => _cache = null;

    public async Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        await SaveAsync(new BenchmarkSettingsDocument(), cancellationToken).ConfigureAwait(false);

    public BenchmarkSettingsAdvisorService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task<BenchmarkSettingsEntry?> GetCachedAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Models.TryGetValue(model, out var entry) ? entry : null;
    }

    public async Task<BenchmarkSettingsEntry> SuggestAsync(
        ModelProfileSummary summary,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        if (CategoryNormalizer.IsEmbeddingModel(summary.Model, category ?? summary.Category))
        {
            return new BenchmarkSettingsEntry
            {
                NumCtx = 0,
                NumPredict = 0,
                Rationale = "Embedding model — uses /api/embed benchmark (latency ms; no generation settings).",
                GeneratedAt = DateTimeOffset.Now.ToString("o")
            };
        }

        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (doc.Models.TryGetValue(summary.Model, out var existing)
            && !string.IsNullOrWhiteSpace(existing.Rationale))
        {
            return NormalizeEntry(existing, summary);
        }

        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.OptimalBenchmarkSettings, cancellationToken)
                .ConfigureAwait(false))
        {
            return NormalizeEntry(DefaultEntry(summary), summary);
        }

        var summarizer = await _summarizer.ResolveAsync(summary.Model, cancellationToken).ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return NormalizeEntry(DefaultEntry(summary), summary);
        }

        var prior = summary.NeedsRetest ? "none" : $"{summary.BestMode} {summary.BestTps:F1} tok/s";
        var prompt = $"""
            Suggest Ollama benchmark settings for AMD Vulkan Windows laptop. Reply JSON only with NumCtx, NumPredict, Rationale fields.
            Model: {summary.Model} ({summary.SizeGB} GB, {summary.ParameterSize})
            Category: {category ?? "unknown"}
            Prior best: {prior}
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 128, 4096, cancellationToken)
                .ConfigureAwait(false);
            var entry = NormalizeEntry(ParseSettings(text) ?? DefaultEntry(summary), summary);
            entry.SummaryModel = summarizer;
            entry.GeneratedAt = DateTimeOffset.Now.ToString("o");
            doc.Models[summary.Model] = entry;
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        catch
        {
            return NormalizeEntry(DefaultEntry(summary), summary);
        }
    }

    public static BenchmarkSettingsEntry NormalizeEntry(
        BenchmarkSettingsEntry entry,
        ModelProfileSummary summary)
    {
        if (CategoryNormalizer.IsEmbeddingModel(summary.Model, summary.Category))
        {
            entry.NumCtx = 0;
            entry.NumPredict = 0;
            entry.Rationale ??= "Embedding model — uses /api/embed benchmark (latency ms; no generation settings).";
            return entry;
        }

        var sizeGb = summary.SizeGB > 0 ? summary.SizeGB : 4;
        var recommended = summary.RecommendedCtx > 0
            ? summary.RecommendedCtx
            : ProfileStoreService.GetRecommendedBenchmarkNumCtx(sizeGb);

        if (entry.NumCtx < 2048)
        {
            entry.NumCtx = recommended;
        }

        entry.NumCtx = Math.Clamp(entry.NumCtx, 2048, 32768);
        entry.NumPredict = Math.Clamp(entry.NumPredict, 8, 512);
        return entry;
    }

    private static BenchmarkSettingsEntry DefaultEntry(ModelProfileSummary summary) =>
        new()
        {
            NumCtx = summary.RecommendedCtx > 0 ? summary.RecommendedCtx : 8192,
            NumPredict = 32,
            Rationale = $"Default for {summary.SizeGB} GB model.",
            GeneratedAt = DateTimeOffset.Now.ToString("o")
        };

    private static BenchmarkSettingsEntry? ParseSettings(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var json = text[start..(end + 1)];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new BenchmarkSettingsEntry
            {
                NumCtx = root.TryGetProperty("NumCtx", out var ctx) ? ctx.GetInt32() : 8192,
                NumPredict = root.TryGetProperty("NumPredict", out var pred) ? pred.GetInt32() : 32,
                Rationale = root.TryGetProperty("Rationale", out var rat) ? rat.GetString() : null,
                GeneratedAt = DateTimeOffset.Now.ToString("o")
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<BenchmarkSettingsDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<BenchmarkSettingsDocument>(
            ConfigPaths.ModelBenchmarkSettingsFile, cancellationToken).ConfigureAwait(false)
            ?? new BenchmarkSettingsDocument();
        _cache.Models ??= new Dictionary<string, BenchmarkSettingsEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private async Task SaveAsync(BenchmarkSettingsDocument doc, CancellationToken cancellationToken)
    {
        doc.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = doc;
        await JsonFileHelper.WriteAsync(ConfigPaths.ModelBenchmarkSettingsFile, doc, cancellationToken)
            .ConfigureAwait(false);
    }
}