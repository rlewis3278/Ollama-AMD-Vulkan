using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.AiAssist.Models;

namespace OllamaToolkit.AiAssist;

public sealed class BenchmarkInsightService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private BenchmarkInsightsDocument? _cache;

    public BenchmarkInsightService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public void ClearCache() => _cache = null;

    public async Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        await SaveAsync(new BenchmarkInsightsDocument(), cancellationToken).ConfigureAwait(false);

    public async Task<BenchmarkInsightsDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<BenchmarkInsightsDocument>(
            ConfigPaths.ModelBenchmarkInsightsFile, cancellationToken).ConfigureAwait(false)
            ?? new BenchmarkInsightsDocument();

        _cache.Models ??= new Dictionary<string, BenchmarkInsightEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    public async Task SaveAsync(BenchmarkInsightsDocument doc, CancellationToken cancellationToken = default)
    {
        doc.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = doc;
        await JsonFileHelper.WriteAsync(ConfigPaths.ModelBenchmarkInsightsFile, doc, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetInsightAsync(string model, CancellationToken cancellationToken = default)
    {
        var entry = await GetEntryAsync(model, cancellationToken).ConfigureAwait(false);
        return entry?.Interpretation;
    }

    public async Task<BenchmarkInsightEntry?> GetEntryAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Models.TryGetValue(model, out var entry) ? entry : null;
    }

    public async Task InterpretProfileAsync(
        ModelProfileSummary summary,
        CancellationToken cancellationToken = default)
    {
        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.BenchmarkInterpreter, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var summarizer = await _summarizer.ResolveAsync(summary.Model, cancellationToken).ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var isEmbed = summary.BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase);
        var modes = summary.Results is null
            ? string.Empty
            : string.Join(", ", summary.Results.Select(r => isEmbed
                ? $"{r.Key}:{r.Value.Status}@{r.Value.EmbedLatencyMs:F1}ms"
                : $"{r.Key}:{r.Value.Status}@{r.Value.GenerationTps:F1}tok/s"));

        var bestMetric = isEmbed
            ? $"{summary.BestEmbedMs:F1} ms/embed"
            : $"{summary.BestTps:F1} tok/s";
        var benchmarkType = isEmbed ? "embedding (/api/embed)" : "generation (/api/generate)";

        var prompt = $"""
            Summarize this Ollama {benchmarkType} benchmark on AMD Vulkan Windows in 2-3 sentences for a laptop user.
            Model: {summary.Model} ({summary.SizeGB} GB)
            Best mode: {summary.BestMode} at {bestMetric}
            Per-mode: {modes}
            Summary:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 128, 4096, cancellationToken)
                .ConfigureAwait(false);
            var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
            doc.Models[summary.Model] = new BenchmarkInsightEntry
            {
                Interpretation = text.Trim(),
                SummaryModel = summarizer,
                GeneratedAt = DateTimeOffset.Now.ToString("o"),
                FailureDiagnosis = doc.Models.TryGetValue(summary.Model, out var existing)
                    ? existing.FailureDiagnosis
                    : null
            };
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal.
        }
    }

    public async Task DiagnoseFailuresAsync(
        ModelProfileSummary summary,
        CancellationToken cancellationToken = default)
    {
        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.FailureDiagnosis, cancellationToken)
                .ConfigureAwait(false)
            || summary.Results is null)
        {
            return;
        }

        var failures = summary.Results
            .Where(r => r.Value.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (failures.Count == 0)
        {
            return;
        }

        var summarizer = await _summarizer.ResolveAsync(summary.Model, cancellationToken).ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var failureText = string.Join("; ", failures.Select(f => $"{f.Key}: {f.Value.Error ?? "unknown"}"));
        var prompt = $"""
            Diagnose why these Ollama benchmark modes failed on AMD Vulkan Windows. One short sentence per mode.
            Model: {summary.Model}
            Failures: {failureText}
            Diagnosis:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 128, 4096, cancellationToken)
                .ConfigureAwait(false);
            var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!doc.Models.TryGetValue(summary.Model, out var entry))
            {
                entry = new BenchmarkInsightEntry();
                doc.Models[summary.Model] = entry;
            }

            entry.FailureDiagnosis ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fail in failures)
            {
                entry.FailureDiagnosis[fail.Key] = text.Trim();
            }

            entry.SummaryModel = summarizer;
            entry.GeneratedAt = DateTimeOffset.Now.ToString("o");
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal.
        }
    }
}