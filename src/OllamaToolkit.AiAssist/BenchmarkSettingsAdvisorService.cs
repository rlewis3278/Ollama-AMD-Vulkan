using System.Text.Json;
using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.Core.Vulkan;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCategory;

namespace OllamaToolkit.AiAssist;

public sealed class BenchmarkSettingsAdvisorService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private readonly VulkanDeviceMap _deviceMap;
    private BenchmarkSettingsDocument? _cache;

    public void ClearCache() => _cache = null;

    public async Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        await SaveAsync(new BenchmarkSettingsDocument(), cancellationToken).ConfigureAwait(false);

    public async Task ClearForModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (doc.Models.Remove(model))
        {
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
        }
    }

    public BenchmarkSettingsAdvisorService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null,
        VulkanDeviceMap? deviceMap = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
        _deviceMap = deviceMap ?? new ModeDefinitionService().DeviceMap;
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
                NumParallelByMode = BenchmarkParallelSettings.EmbedDefaults(),
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
            Suggest Ollama benchmark settings for AMD Vulkan Windows laptop. Reply JSON only.
            Required fields: NumCtx (int), NumPredict (int), NumParallelByMode (object), Rationale (string).
            NumParallelByMode keys: CPU, APU, GPU, Hybrid, ROCm — integer values 1-4 only.
            OLLAMA_NUM_PARALLEL reserves VRAM per concurrent model slot. Benchmarks run one request at a time;
            parallel >1 affects memory headroom when chatting after launch, not benchmark throughput.
            Model: {summary.Model} ({summary.SizeGB} GB, {summary.ParameterSize})
            Category: {category ?? "unknown"}
            Prior best: {prior}
            Hardware: APU Vulkan {_deviceMap.ApuVulkanIndex} ({_deviceMap.ApuName}), GPU Vulkan {_deviceMap.GpuVulkanIndex} ({_deviceMap.GpuName}).
            CPU mode: parallel 1. Large models (>=18 GB): prefer 1 on GPU modes. Small models on dGPU may use 2-3 if VRAM allows.
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 192, 4096, cancellationToken)
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
            entry.NumParallelByMode = BenchmarkParallelSettings.EmbedDefaults();
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
        entry.NumParallelByMode = BenchmarkParallelSettings.NormalizeByMode(
            entry.NumParallelByMode, sizeGb, isEmbed: false);
        return entry;
    }

    private static BenchmarkSettingsEntry DefaultEntry(ModelProfileSummary summary)
    {
        var sizeGb = summary.SizeGB > 0 ? summary.SizeGB : 4;
        return new BenchmarkSettingsEntry
        {
            NumCtx = summary.RecommendedCtx > 0 ? summary.RecommendedCtx : 8192,
            NumPredict = 32,
            NumParallelByMode = BenchmarkParallelSettings.DefaultByMode(sizeGb),
            Rationale = $"Default for {summary.SizeGB} GB model.",
            GeneratedAt = DateTimeOffset.Now.ToString("o")
        };
    }

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
                NumParallelByMode = ParseParallelByMode(root),
                Rationale = root.TryGetProperty("Rationale", out var rat) ? rat.GetString() : null,
                GeneratedAt = DateTimeOffset.Now.ToString("o")
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, int>? ParseParallelByMode(JsonElement root)
    {
        if (!root.TryGetProperty("NumParallelByMode", out var parallel)
            || parallel.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parallel.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var value))
            {
                map[property.Name] = BenchmarkParallelSettings.Clamp(value);
            }
        }

        return map.Count == 0 ? null : map;
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