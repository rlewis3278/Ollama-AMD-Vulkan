using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCategory;

namespace OllamaToolkit.AiAssist;

public sealed class ModelAdvisorService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;

    public ModelAdvisorService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task<IReadOnlyList<ModelAdvisorRecommendation>> RecommendInstalledAsync(
        string intent,
        IReadOnlyList<ModelProfileSummary> installed,
        IReadOnlyDictionary<string, string> categories,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(intent) || installed.Count == 0)
        {
            return Array.Empty<ModelAdvisorRecommendation>();
        }

        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.ModelPickerAdvisor, cancellationToken)
                .ConfigureAwait(false))
        {
            return FallbackRank(intent, installed, categories);
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return FallbackRank(intent, installed, categories);
        }

        var catalog = string.Join("\n", installed.Select(s =>
        {
            var lib = s.Model.Split(':')[0];
            var cat = categories.TryGetValue(lib, out var c) ? c : string.Empty;
            var bench = s.NeedsRetest ? "untested" : $"{s.BestMode} {s.BestTps:F1} tok/s";
            return $"- {s.Model} | {cat} | {s.SizeGB} GB | {bench}";
        }));

        var prompt = $"""
            User intent: {intent.Trim()}
            Pick the best installed Ollama models for this intent. Reply with ONLY model names, one per line, best first (max 8).
            Installed:
            {catalog}
            Recommended:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 128, 4096, cancellationToken)
                .ConfigureAwait(false);
            return ParseRecommendations(text, installed, categories);
        }
        catch
        {
            return FallbackRank(intent, installed, categories);
        }
    }

    private static IReadOnlyList<ModelAdvisorRecommendation> ParseRecommendations(
        string text,
        IReadOnlyList<ModelProfileSummary> installed,
        IReadOnlyDictionary<string, string> categories)
    {
        var byName = installed.ToDictionary(s => s.Model, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModelAdvisorRecommendation>();
        var rank = 1;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = line.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ');
            var match = byName.Keys.FirstOrDefault(k =>
                token.Contains(k, StringComparison.OrdinalIgnoreCase)
                || k.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match is null || result.Any(r => r.Model.Equals(match, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var s = byName[match];
            var lib = s.Model.Split(':')[0];
            result.Add(new ModelAdvisorRecommendation
            {
                Model = s.Model,
                BestMode = s.BestMode,
                BestTps = s.BestTps,
                Category = categories.TryGetValue(lib, out var c) ? c : string.Empty,
                Rank = rank++
            });
        }

        return result.Count > 0 ? result : FallbackRank(string.Empty, installed, categories);
    }

    private static IReadOnlyList<ModelAdvisorRecommendation> FallbackRank(
        string intent,
        IReadOnlyList<ModelProfileSummary> installed,
        IReadOnlyDictionary<string, string> categories)
    {
        var q = intent.Trim();
        var ranked = installed
            .OrderByDescending(s => Score(s, q, categories))
            .ThenByDescending(s => s.BestTps)
            .Take(8)
            .Select((s, i) =>
            {
                var lib = s.Model.Split(':')[0];
                return new ModelAdvisorRecommendation
                {
                    Model = s.Model,
                    BestMode = s.BestMode,
                    BestTps = s.BestTps,
                    Category = categories.TryGetValue(lib, out var c) ? c : string.Empty,
                    Rank = i + 1
                };
            })
            .ToList();
        return ranked;
    }

    private static int Score(ModelProfileSummary s, string intent, IReadOnlyDictionary<string, string> categories)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return 0;
        }

        var lib = s.Model.Split(':')[0];
        var cat = categories.TryGetValue(lib, out var c) ? c : string.Empty;
        var blob = $"{s.Model} {cat}".ToLowerInvariant();
        var words = intent.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Count(w => blob.Contains(w, StringComparison.Ordinal));
    }
}