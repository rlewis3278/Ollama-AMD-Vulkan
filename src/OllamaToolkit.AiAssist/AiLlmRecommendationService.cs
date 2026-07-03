using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCatalog.Models;
using OllamaToolkit.ModelCategory;

namespace OllamaToolkit.AiAssist;

public sealed class AiLlmRecommendationService
{
    public const string DefaultIntent =
        "General-purpose LLMs for chat, coding, and reasoning on this PC.";

    private const int MaxListSize = 15;
    private const int MaxUninstalledCandidates = 60;
    private const double AiWeight = 0.55;
    private const double BenchWeight = 0.45;

    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private AiLlmRecommendationsDocument? _cache;

    public AiLlmRecommendationService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task<AiLlmRecommendationsDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<AiLlmRecommendationsDocument>(
            ConfigPaths.AiLlmRecommendationsFile, cancellationToken).ConfigureAwait(false);
        return _cache;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _cache = null;
        if (File.Exists(ConfigPaths.AiLlmRecommendationsFile))
        {
            File.Delete(ConfigPaths.AiLlmRecommendationsFile);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void ClearCache() => _cache = null;

    public async Task<AiLlmRecommendationsDocument> GenerateAsync(
        string? intent,
        IReadOnlyList<ModelProfileSummary> installedSummaries,
        IReadOnlyList<LibraryCatalogEntry> catalogEntries,
        IReadOnlyDictionary<string, string> categories,
        IReadOnlySet<string> installedTagNames,
        CancellationToken cancellationToken = default)
    {
        var resolvedIntent = string.IsNullOrWhiteSpace(intent) ? DefaultIntent : intent.Trim();
        var installedCandidates = BuildInstalledCandidates(installedSummaries, categories);
        var uninstalledCandidates = BuildUninstalledCandidates(
            catalogEntries, categories, installedTagNames);

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var useAi = summarizer is not null
            && await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false)
            && await _settings.IsFeatureEnabledAsync(AiFeatureKeys.ModelPickerAdvisor, cancellationToken)
                .ConfigureAwait(false);

        List<string> installedAiOrder;
        List<string> uninstalledAiOrder;
        if (useAi)
        {
            installedAiOrder = await RankInstalledWithAiAsync(
                resolvedIntent, installedCandidates, summarizer!, cancellationToken)
                .ConfigureAwait(false);
            uninstalledAiOrder = await RankUninstalledWithAiAsync(
                resolvedIntent, uninstalledCandidates, summarizer!, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            installedAiOrder = FallbackInstalledOrder(installedCandidates);
            uninstalledAiOrder = FallbackUninstalledOrder(uninstalledCandidates);
        }

        var maxTps = installedCandidates
            .Where(c => !c.NeedsRetest && c.BestTps > 0)
            .Select(c => c.BestTps)
            .DefaultIfEmpty(0)
            .Max();

        var catalogIndex = CatalogMetadataLookup.BuildPullTagIndex(catalogEntries);
        var installedEntries = BuildInstalledEntries(
            installedCandidates, installedAiOrder, maxTps, catalogIndex);
        var uninstalledEntries = BuildUninstalledEntries(
            uninstalledCandidates, uninstalledAiOrder);

        var doc = new AiLlmRecommendationsDocument
        {
            Intent = resolvedIntent,
            GeneratedAt = DateTimeOffset.Now.ToString("o"),
            SummaryModel = summarizer,
            Installed = installedEntries,
            Uninstalled = uninstalledEntries
        };

        await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
        return doc;
    }

    private static List<InstalledCandidate> BuildInstalledCandidates(
        IReadOnlyList<ModelProfileSummary> summaries,
        IReadOnlyDictionary<string, string> categories)
    {
        var result = new List<InstalledCandidate>();
        foreach (var s in summaries)
        {
            var lib = s.Model.Split(':')[0];
            var category = categories.TryGetValue(lib, out var c) ? c : s.Category;
            if (CategoryNormalizer.IsEmbeddingModel(s.Model, category))
            {
                continue;
            }

            result.Add(new InstalledCandidate(
                s.Model,
                string.IsNullOrWhiteSpace(category) ? "-" : category,
                s.BestMode,
                s.BenchmarkKind,
                s.BestTps,
                s.BestEmbedMs,
                s.NeedsRetest,
                s.SizeGB,
                s.RecommendedCtx));
        }

        return result;
    }

    private static List<UninstalledCandidate> BuildUninstalledCandidates(
        IReadOnlyList<LibraryCatalogEntry> entries,
        IReadOnlyDictionary<string, string> categories,
        IReadOnlySet<string> installedTagNames)
    {
        var result = new List<UninstalledCandidate>();
        foreach (var e in entries)
        {
            var libraryName = CatalogEntryNames.LibraryName(e);
            var pullTag = CatalogEntryNames.PullTag(e);
            if (installedTagNames.Contains(pullTag) || installedTagNames.Contains(e.Name))
            {
                continue;
            }

            var category = !string.IsNullOrWhiteSpace(e.Category)
                ? e.Category
                : categories.TryGetValue(libraryName, out var c)
                    ? c
                    : CategoryNormalizer.HeuristicCategory(libraryName, e.Description, e.Tags);

            if (CategoryNormalizer.IsEmbeddingModel(libraryName, category))
            {
                continue;
            }

            result.Add(new UninstalledCandidate(
                libraryName,
                pullTag,
                string.IsNullOrWhiteSpace(category) ? "-" : category,
                string.IsNullOrWhiteSpace(e.ParameterSize) ? "-" : e.ParameterSize,
                CatalogMetadataLookup.ResolveSizeUsage(e),
                CatalogMetadataLookup.ResolveContext(e),
                CatalogMetadataLookup.ResolveInput(e),
                string.IsNullOrWhiteSpace(e.FileSize) ? "-" : e.FileSize,
                DescriptionStoreService.NormalizeListDescription(e.Description)));
        }

        return result;
    }

    private async Task<List<string>> RankInstalledWithAiAsync(
        string intent,
        IReadOnlyList<InstalledCandidate> candidates,
        string summarizer,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var catalog = string.Join("\n", candidates.Select(c =>
        {
            var bench = c.NeedsRetest || string.IsNullOrEmpty(c.BestMode)
                ? "untested"
                : $"{c.BestMode} {BenchmarkMetricFormatter.Format(c.BenchmarkKind, c.BestTps, c.BestEmbedMs)}";
            return $"- {c.Model} | {c.Category} | {c.SizeGb:F1} GB | {bench}";
        }));

        var prompt = $"""
            User intent: {intent}
            Rank the best installed Ollama LLMs for this intent. Exclude embedding models.
            Reply with ONLY model names, one per line, best first (max {MaxListSize}).
            Installed:
            {catalog}
            Ranked:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 192, 4096, cancellationToken)
                .ConfigureAwait(false);
            return ParseRankedNames(text, candidates.Select(c => c.Model).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FallbackInstalledOrder(candidates);
        }
    }

    private async Task<List<string>> RankUninstalledWithAiAsync(
        string intent,
        IReadOnlyList<UninstalledCandidate> candidates,
        string summarizer,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var subset = candidates
            .OrderBy(c => CategoryNormalizer.GetSortOrder(c.Category))
            .ThenBy(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxUninstalledCandidates)
            .ToList();

        var catalog = string.Join("\n", subset.Select(c =>
            $"- {c.LibraryName} | {c.Category} | {c.ParameterSize} | {c.FileSize}"));

        var prompt = $"""
            User intent: {intent}
            Rank the best Ollama library models to download for this intent. Exclude embedding models.
            Reply with ONLY library names, one per line, best first (max {MaxListSize}).
            Not installed:
            {catalog}
            Ranked:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 256, 4096, cancellationToken)
                .ConfigureAwait(false);
            return ParseRankedNames(text, subset.Select(c => c.LibraryName).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FallbackUninstalledOrder(candidates);
        }
    }

    private static List<string> ParseRankedNames(string text, IReadOnlyList<string> validNames)
    {
        var valid = new HashSet<string>(validNames, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = line.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ');
            var match = valid.FirstOrDefault(v =>
                token.Contains(v, StringComparison.OrdinalIgnoreCase)
                || v.Contains(token, StringComparison.OrdinalIgnoreCase)
                || token.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    ?.Equals(v.Split(':')[0], StringComparison.OrdinalIgnoreCase) == true);
            if (match is not null && !result.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(match);
            }
        }

        return result;
    }

    private static List<string> FallbackInstalledOrder(IReadOnlyList<InstalledCandidate> candidates) =>
        candidates
            .OrderByDescending(c => c.NeedsRetest ? 0 : c.BestTps)
            .ThenBy(c => CategoryNormalizer.GetSortOrder(c.Category))
            .ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Model)
            .ToList();

    private static List<string> FallbackUninstalledOrder(IReadOnlyList<UninstalledCandidate> candidates) =>
        candidates
            .OrderBy(c => CategoryNormalizer.GetSortOrder(c.Category))
            .ThenBy(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.LibraryName)
            .ToList();

    private static List<AiRecommendedLlmEntry> BuildInstalledEntries(
        IReadOnlyList<InstalledCandidate> candidates,
        IReadOnlyList<string> aiOrder,
        double maxTps,
        IReadOnlyDictionary<string, LibraryCatalogEntry> catalogIndex)
    {
        var byModel = candidates.ToDictionary(c => c.Model, StringComparer.OrdinalIgnoreCase);
        var aiRankByModel = aiOrder
            .Select((name, index) => (name, rank: index + 1))
            .ToDictionary(x => x.name, x => x.rank, StringComparer.OrdinalIgnoreCase);
        var maxAiRank = Math.Max(aiOrder.Count, 1);

        var scored = new List<AiRecommendedLlmEntry>();
        foreach (var c in candidates)
        {
            var aiRank = aiRankByModel.TryGetValue(c.Model, out var r) ? r : maxAiRank + 1;
            var aiScore = aiRank <= maxAiRank ? (maxAiRank - aiRank + 1.0) / maxAiRank : 0;
            var benchScore = !c.NeedsRetest && c.BestTps > 0 && maxTps > 0
                ? Math.Min(1.0, c.BestTps / maxTps)
                : 0;
            var composite = AiWeight * aiScore + BenchWeight * benchScore;
            var metric = c.NeedsRetest || string.IsNullOrEmpty(c.BestMode)
                ? "-"
                : BenchmarkMetricFormatter.Format(c.BenchmarkKind, c.BestTps, c.BestEmbedMs);

            CatalogMetadataLookup.TryGetForModel(catalogIndex, c.Model, out var catalogEntry);
            var installedSize = c.SizeGb > 0 ? ModelSizeFormatter.FormatGb(c.SizeGb) : null;
            var sizeUsage = CatalogMetadataLookup.ResolveSizeUsage(catalogEntry, installedSize);
            var contextDisplay = CatalogMetadataLookup.ResolveContext(catalogEntry, c.RecommendedCtx);
            var inputModalities = CatalogMetadataLookup.ResolveInput(catalogEntry);
            var fileSize = catalogEntry is not null
                && !string.IsNullOrWhiteSpace(catalogEntry.FileSize)
                && catalogEntry.FileSize != "-"
                ? catalogEntry.FileSize
                : installedSize ?? "-";

            scored.Add(new AiRecommendedLlmEntry
            {
                ModelOrLibrary = c.Model,
                PullTag = c.Model.Contains(':', StringComparison.Ordinal) ? c.Model : $"{c.Model}:latest",
                Category = c.Category,
                SizeUsage = sizeUsage,
                FileSize = fileSize,
                ContextDisplay = contextDisplay,
                InputModalities = inputModalities,
                ContextSortKey = CatalogMetadataLookup.ResolveContextSortKey(catalogEntry, c.RecommendedCtx),
                FileSizeSortKey = c.SizeGb > 0
                    ? (long)(c.SizeGb * 1_073_741_824.0)
                    : CatalogMetadataLookup.ResolveFileSizeSortKey(catalogEntry, fileSize),
                MetricSortKey = CatalogMetadataLookup.ResolveMetricSortKey(
                    c.BenchmarkKind, c.BestTps, c.BestEmbedMs),
                AiRank = aiRank,
                BenchmarkScore = benchScore,
                CompositeScore = composite,
                BestMode = string.IsNullOrEmpty(c.BestMode) ? "-" : c.BestMode,
                MetricDisplay = metric,
                AiNote = BuildInstalledNote(c, aiRank)
            });
        }

        return AssignDisplayRanks(
            scored
                .OrderByDescending(e => e.CompositeScore)
                .ThenBy(e => e.AiRank)
                .Take(MaxListSize)
                .ToList());
    }

    private static List<AiRecommendedLlmEntry> BuildUninstalledEntries(
        IReadOnlyList<UninstalledCandidate> candidates,
        IReadOnlyList<string> aiOrder)
    {
        var variantsByLibrary = candidates
            .GroupBy(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(v => v.PullTag, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var maxAiRank = Math.Max(aiOrder.Count, 1);
        var scored = new List<AiRecommendedLlmEntry>();
        var usedPullTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, index) in aiOrder.Select((n, i) => (n, i)))
        {
            if (!variantsByLibrary.TryGetValue(name, out var variants))
            {
                continue;
            }

            var chosen = variants.FirstOrDefault(v => v.PullTag.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
                ?? variants[0];
            var aiRank = index + 1;
            var aiScore = (maxAiRank - aiRank + 1.0) / maxAiRank;
            scored.Add(ToUninstalledRecommendation(chosen, aiRank, aiScore,
                $"Suggested download for {chosen.Category.ToLowerInvariant()} workloads."));
            usedPullTags.Add(chosen.PullTag);
        }

        foreach (var c in candidates)
        {
            if (usedPullTags.Contains(c.PullTag))
            {
                continue;
            }

            scored.Add(ToUninstalledRecommendation(c, maxAiRank + 1, 0, "Not ranked by AI."));
            usedPullTags.Add(c.PullTag);
        }

        return AssignDisplayRanks(
            scored
                .OrderByDescending(e => e.CompositeScore)
                .ThenBy(e => e.AiRank)
                .Take(MaxListSize)
                .ToList());
    }

    private static List<AiRecommendedLlmEntry> AssignDisplayRanks(List<AiRecommendedLlmEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].DisplayRank = i + 1;
        }

        return entries;
    }

    private static string BuildInstalledNote(InstalledCandidate c, int aiRank)
    {
        if (c.NeedsRetest)
        {
            return "Installed — run Testing Suite for benchmark-backed ranking.";
        }

        return aiRank <= 3
            ? $"Top pick — {c.BestMode} @ {BenchmarkMetricFormatter.Format(c.BenchmarkKind, c.BestTps, c.BestEmbedMs)}"
            : $"Tested on {c.BestMode}.";
    }

    private async Task SaveAsync(AiLlmRecommendationsDocument doc, CancellationToken cancellationToken)
    {
        doc.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = doc;
        await JsonFileHelper.WriteAsync(ConfigPaths.AiLlmRecommendationsFile, doc, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record InstalledCandidate(
        string Model,
        string Category,
        string BestMode,
        string BenchmarkKind,
        double BestTps,
        double BestEmbedMs,
        bool NeedsRetest,
        double SizeGb,
        int RecommendedCtx);

    private static AiRecommendedLlmEntry ToUninstalledRecommendation(
        UninstalledCandidate candidate,
        int aiRank,
        double compositeScore,
        string note) =>
        new()
        {
            ModelOrLibrary = candidate.PullTag,
            PullTag = candidate.PullTag,
            Category = candidate.Category,
            ParameterSize = candidate.ParameterSize,
            SizeUsage = candidate.SizeUsage,
            FileSize = candidate.FileSize,
            ContextDisplay = candidate.ContextDisplay,
            InputModalities = candidate.InputModalities,
            ContextSortKey = CatalogMetadataLookup.ParseContextSortKey(candidate.ContextDisplay),
            FileSizeSortKey = CatalogMetadataLookup.ResolveFileSizeSortKey(null, candidate.FileSize),
            MetricSortKey = 0,
            AiRank = aiRank,
            BenchmarkScore = 0,
            CompositeScore = compositeScore,
            AiNote = note
        };

    private sealed record UninstalledCandidate(
        string LibraryName,
        string PullTag,
        string Category,
        string ParameterSize,
        string SizeUsage,
        string ContextDisplay,
        string InputModalities,
        string FileSize,
        string Description);
}