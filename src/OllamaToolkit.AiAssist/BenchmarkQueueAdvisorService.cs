using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;

namespace OllamaToolkit.AiAssist;

public sealed class BenchmarkQueueAdvisorService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;

    public BenchmarkQueueAdvisorService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public sealed record PrioritizedQueue(IReadOnlyList<string> Models, string Rationale);

    public sealed record UndownloadQueueItem(
        string LibraryName,
        string PullTag,
        long FileSizeBytes,
        bool HasKnownFileSize);

    public sealed record PrioritizedUndownloadQueue(
        IReadOnlyList<UndownloadQueueItem> Candidates,
        string Rationale);

    public async Task<PrioritizedQueue> PrioritizeAsync(
        IReadOnlyList<string> models,
        IReadOnlyList<ModelProfileSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        if (models.Count <= 1)
        {
            return new PrioritizedQueue(models, "Single model queue.");
        }

        var byName = summaries.ToDictionary(s => s.Model, StringComparer.OrdinalIgnoreCase);
        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.TestQueuePrioritization, cancellationToken)
                .ConfigureAwait(false))
        {
            var fallback = FallbackOrder(models, byName);
            return new PrioritizedQueue(fallback, "Ordered by size (smallest first).");
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            var fallback = FallbackOrder(models, byName);
            return new PrioritizedQueue(fallback, "Ordered by size (smallest first).");
        }

        var catalog = string.Join("\n", models.Select(m =>
        {
            if (!byName.TryGetValue(m, out var s))
            {
                return $"- {m} | unknown";
            }

            var fails = s.Results?.Count(r =>
                r.Value.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
                || r.Value.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) ?? 0;
            return $"- {m} | {s.SizeGB} GB | fails={fails} | {(s.NeedsRetest ? "untested" : "tested")}";
        }));

        var prompt = $"""
            Order these Ollama models for benchmark testing on AMD Vulkan Windows. Test quick wins and small models first.
            Reply with model names one per line, then a blank line, then one sentence rationale.
            Models:
            {catalog}
            Order:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 256, 4096, cancellationToken)
                .ConfigureAwait(false);
            var (ordered, rationale) = ParseOrder(text, models);
            return new PrioritizedQueue(ordered, rationale);
        }
        catch
        {
            var fallback = FallbackOrder(models, byName);
            return new PrioritizedQueue(fallback, "Ordered by size (smallest first).");
        }
    }

    private static (IReadOnlyList<string> Ordered, string Rationale) ParseOrder(
        string text,
        IReadOnlyList<string> models)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        var rationale = lines.Count > models.Count ? lines[^1] : "AI-prioritized test queue.";
        var ordered = new List<string>();
        foreach (var line in lines.TakeWhile(l => !l.Contains('.')))
        {
            var token = line.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ');
            var match = models.FirstOrDefault(m =>
                token.Contains(m, StringComparison.OrdinalIgnoreCase)
                || m.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ordered.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(match);
            }
        }

        foreach (var m in models)
        {
            if (!ordered.Contains(m, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(m);
            }
        }

        return (ordered, rationale);
    }

    public async Task<PrioritizedUndownloadQueue> PrioritizeUndownloadAsync(
        IReadOnlyList<UndownloadQueueItem> candidates,
        IReadOnlyDictionary<string, string> categoriesByLibrary,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count <= 1)
        {
            return new PrioritizedUndownloadQueue(
                EnforceUndownloadFileSizeOrder(candidates, candidates),
                "Single model queue.");
        }

        var fallback = EnforceUndownloadFileSizeOrder(candidates, candidates);
        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.TestQueuePrioritization, cancellationToken)
                .ConfigureAwait(false))
        {
            return new PrioritizedUndownloadQueue(
                fallback,
                "Ordered by catalog file size (smallest first, unknown size last).");
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PrioritizedUndownloadQueue(
                fallback,
                "Ordered by catalog file size (smallest first, unknown size last).");
        }

        var catalog = string.Join("\n", candidates.Select(c =>
        {
            var sizeLabel = c.HasKnownFileSize
                ? ModelSizeFormatter.FormatBytes(c.FileSizeBytes)
                : "unknown";
            categoriesByLibrary.TryGetValue(c.LibraryName, out var category);
            var categoryLabel = string.IsNullOrWhiteSpace(category) ? "uncategorized" : category;
            return $"- {c.LibraryName} | pull={c.PullTag} | {sizeLabel} | {categoryLabel} | untested";
        }));

        var prompt = $"""
            Order these undownloaded Ollama library models for benchmark testing on AMD Vulkan Windows.
            Rules (strict):
            1. Models with a known catalog file size must run smallest to largest.
            2. Models with unknown or missing catalog file size must run after all known-size models.
            3. Prefer quick wins and embedding/smaller models when file sizes are similar.
            Reply with library names one per line, then a blank line, then one sentence rationale.
            Models:
            {catalog}
            Order:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 256, 4096, cancellationToken)
                .ConfigureAwait(false);
            var (orderedNames, rationale) = ParseUndownloadOrder(text, candidates);
            var aiPreference = new List<UndownloadQueueItem>();
            foreach (var name in orderedNames)
            {
                var item = candidates.FirstOrDefault(c =>
                    c.LibraryName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (item is not null
                    && !aiPreference.Any(c =>
                        c.LibraryName.Equals(item.LibraryName, StringComparison.OrdinalIgnoreCase)))
                {
                    aiPreference.Add(item);
                }
            }

            return new PrioritizedUndownloadQueue(
                EnforceUndownloadFileSizeOrder(aiPreference, candidates),
                rationale);
        }
        catch
        {
            return new PrioritizedUndownloadQueue(
                fallback,
                "Ordered by catalog file size (smallest first, unknown size last).");
        }
    }

    private static (IReadOnlyList<string> Ordered, string Rationale) ParseUndownloadOrder(
        string text,
        IReadOnlyList<UndownloadQueueItem> candidates)
    {
        var libraryNames = candidates.Select(c => c.LibraryName).ToList();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        var rationale = lines.Count > libraryNames.Count ? lines[^1] : "AI-prioritized undownload test queue.";
        var ordered = new List<string>();
        foreach (var line in lines.TakeWhile(l => !l.Contains('.')))
        {
            var token = line.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ');
            var match = libraryNames.FirstOrDefault(name =>
                token.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ordered.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(match);
            }
        }

        foreach (var name in libraryNames)
        {
            if (!ordered.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(name);
            }
        }

        return (ordered, rationale);
    }

    private static IReadOnlyList<UndownloadQueueItem> EnforceUndownloadFileSizeOrder(
        IReadOnlyList<UndownloadQueueItem> aiPreference,
        IReadOnlyList<UndownloadQueueItem> candidates)
    {
        var aiRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < aiPreference.Count; i++)
        {
            if (!aiRank.ContainsKey(aiPreference[i].LibraryName))
            {
                aiRank[aiPreference[i].LibraryName] = i;
            }
        }

        return candidates
            .OrderBy(c => c.HasKnownFileSize ? 0 : 1)
            .ThenBy(c => c.FileSizeBytes)
            .ThenBy(c => aiRank.TryGetValue(c.LibraryName, out var rank) ? rank : int.MaxValue)
            .ThenBy(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> FallbackOrder(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, ModelProfileSummary> byName) =>
        models.OrderBy(m => byName.TryGetValue(m, out var s) ? s.SizeGB : 999).ToList();
}