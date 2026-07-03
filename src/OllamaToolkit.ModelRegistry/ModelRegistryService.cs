using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCatalog.Models;
using OllamaToolkit.ModelCategory;
using OllamaToolkit.ModelCategory.Models;
using OllamaToolkit.ModelRegistry.Models;

namespace OllamaToolkit.ModelRegistry;

public sealed class ModelRegistryService
{
    private readonly LibraryCatalogStoreService _catalogStore;
    private readonly DescriptionStoreService _descriptions;
    private readonly UsageCategoryStoreService _categories;
    private readonly ProfileStoreService _profiles;
    private readonly OllamaApiClient _apiClient;

    public ModelRegistryService(
        LibraryCatalogStoreService? catalogStore = null,
        DescriptionStoreService? descriptions = null,
        UsageCategoryStoreService? categories = null,
        ProfileStoreService? profiles = null,
        OllamaApiClient? apiClient = null)
    {
        _catalogStore = catalogStore ?? new LibraryCatalogStoreService();
        _descriptions = descriptions ?? new DescriptionStoreService(apiClient: apiClient);
        _categories = categories ?? new UsageCategoryStoreService();
        _profiles = profiles ?? new ProfileStoreService(apiClient);
        _apiClient = apiClient ?? new OllamaApiClient();
    }

    public async Task<IReadOnlyList<CatalogRowViewModel>> GetCatalogRowsAsync(
        string? search = null,
        string? categoryFilter = null,
        CatalogDescriptionDisplayMode descriptionMode = CatalogDescriptionDisplayMode.Download,
        CancellationToken cancellationToken = default)
    {
        var entries = await _catalogStore.GetEntriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var categoryDoc = await _categories.LoadAsync(cancellationToken).ConfigureAwait(false);
        var tagsSnapshot = await _apiClient.FetchTagsSnapshotAsync(forceRefresh: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var installed = tagsSnapshot.Reachable
            ? tagsSnapshot.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installedSizes = tagsSnapshot.Reachable
            ? BuildInstalledFileSizeMap(tagsSnapshot.Tags)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptionDoc = await _descriptions.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profileDoc = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<CatalogRowViewModel>();
        foreach (var e in entries)
        {
            var libraryName = CatalogEntryNames.LibraryName(e);
            var pullTag = CatalogEntryNames.PullTag(e);
            string category;
            if (categoryDoc.Models.TryGetValue(libraryName, out var catEntry)
                && !string.IsNullOrWhiteSpace(catEntry.Category))
            {
                category = CategoryNormalizer.FormatDisplay(catEntry.Category, catEntry.Subcategory);
            }
            else if (!string.IsNullOrWhiteSpace(e.Category))
            {
                category = e.Category;
            }
            else
            {
                category = CategoryNormalizer.HeuristicCategory(libraryName, e.Description, e.Tags);
            }
            var downloadDescription = string.IsNullOrWhiteSpace(e.Description)
                ? string.Empty
                : e.Description.Trim();
            var aiDescription = descriptionDoc.Models.TryGetValue(libraryName, out var aiEntry)
                && !string.IsNullOrWhiteSpace(aiEntry.ListDescription)
                ? aiEntry.ListDescription!.Trim()
                : string.Empty;
            var listDesc = !string.IsNullOrEmpty(e.ListDescription)
                ? e.ListDescription
                : DescriptionStoreService.NormalizeListDescription(e.Description);
            var displayDescription = descriptionMode == CatalogDescriptionDisplayMode.Ai
                ? (string.IsNullOrWhiteSpace(aiDescription) ? "(not summarized)" : aiDescription)
                : downloadDescription;

            var fileSize = CatalogFileSizeDisplay.GetDisplayLabel(e);
            if ((fileSize == "-" || string.IsNullOrWhiteSpace(fileSize))
                && installedSizes.TryGetValue(pullTag, out var installedTagSize))
            {
                fileSize = installedTagSize;
            }
            else if ((fileSize == "-" || string.IsNullOrWhiteSpace(fileSize))
                     && installedSizes.TryGetValue(libraryName, out var installedLibrarySize))
            {
                fileSize = installedLibrarySize;
            }

            var isInstalled = tagsSnapshot.Reachable
                && (installed.Contains(pullTag) || installed.Contains(e.Name));
            var (bestMode, bestTps) = ResolveCatalogBenchmarkDisplay(pullTag, profileDoc);
            if (bestMode == "-")
            {
                (bestMode, bestTps) = ResolveCatalogBenchmarkDisplay(libraryName, profileDoc);
            }

            var hasTestedProfile = bestMode != "-";
            var benchmarkCtx = ResolveBenchmarkContext(pullTag, profileDoc);
            if (benchmarkCtx <= 0)
            {
                benchmarkCtx = ResolveBenchmarkContext(libraryName, profileDoc);
            }

            var formattedSize = ModelSizeFormatter.FormatSizeLabel(fileSize);
            var sizeUsage = CatalogMetadataLookup.ResolveSizeUsage(e, formattedSize);
            var contextDisplay = CatalogMetadataLookup.ResolveContext(e, benchmarkCtx);
            var (_, _, benchmarkKind, bestTpsValue, bestEmbedMs) =
                ResolveCatalogBenchmarkMetrics(pullTag, profileDoc);
            if (bestTpsValue <= 0 && bestEmbedMs <= 0)
            {
                (_, _, benchmarkKind, bestTpsValue, bestEmbedMs) =
                    ResolveCatalogBenchmarkMetrics(libraryName, profileDoc);
            }

            rows.Add(new CatalogRowViewModel
            {
                Name = pullTag,
                LibraryName = libraryName,
                DefaultPullTag = pullTag,
                IsCloudOnly = e.IsCloudOnly,
                Description = e.Description,
                ListDescription = listDesc,
                DownloadDescription = downloadDescription,
                AiDescription = aiDescription,
                DisplayDescription = displayDescription,
                Category = category,
                ParameterSize = e.ParameterSize,
                SizeUsage = sizeUsage,
                FileSize = formattedSize,
                FileSizeSortKey = CatalogMetadataLookup.ResolveFileSizeSortKey(e, sizeUsage),
                ContextDisplay = contextDisplay,
                ContextSortKey = CatalogMetadataLookup.ResolveContextSortKey(e, benchmarkCtx),
                InputModalities = CatalogMetadataLookup.ResolveInput(e),
                Tags = e.Tags,
                Installed = isInstalled,
                InstalledDisplay = tagsSnapshot.Reachable
                    ? isInstalled ? "Yes" : "No"
                    : "Unknown",
                BestMode = bestMode,
                BestTps = bestTps,
                BestMetricSortKey = CatalogMetadataLookup.ResolveMetricSortKey(
                    benchmarkKind, bestTpsValue, bestEmbedMs),
                RefreshHighlight = !isInstalled && hasTestedProfile
                    ? CatalogRowRefreshHighlight.TestedUndownload
                    : CatalogRowRefreshHighlight.None,
                SortOrder = CategoryNormalizer.GetSortOrder(category)
            });
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(r => CatalogRowSearch.Matches(r, search)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(categoryFilter) && categoryFilter != "All")
        {
            rows = rows.Where(r =>
                    CategoryNormalizer.ExtractMajorCategory(r.Category)
                        .Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
    }

    public async Task<IReadOnlyList<TestResultRowViewModel>> GetTestResultRowsAsync(
        CancellationToken cancellationToken = default)
    {
        var summaries = await _profiles.GetAllSummariesAsync(cancellationToken).ConfigureAwait(false);
        var categoryDoc = await _categories.LoadAsync(cancellationToken).ConfigureAwait(false);
        categoryDoc.Models ??= new Dictionary<string, UsageCategoryEntry>(StringComparer.OrdinalIgnoreCase);
        var (_, installed) = await GetInstalledNameSetAsync(cancellationToken).ConfigureAwait(false);
        var catalogIndex = CatalogMetadataLookup.BuildPullTagIndex(
            (await _catalogStore.GetEntriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).ToList());

        return summaries
            .Where(s => s.Results is { Count: > 0 })
            .Select(s =>
            {
                var category = string.Empty;
                var libName = s.Model.Split(':')[0];
                if (categoryDoc.Models.TryGetValue(libName, out var cat))
                {
                    category = string.IsNullOrWhiteSpace(cat.Category)
                        ? string.Empty
                        : CategoryNormalizer.FormatDisplay(cat.Category, cat.Subcategory);
                }

                var benchmarkKind = string.IsNullOrWhiteSpace(s.BenchmarkKind)
                    ? BenchmarkKinds.Generate
                    : s.BenchmarkKind;
                var isEmbed = benchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase);
                var allFailed = BenchmarkCompletion.IsAllModesFailed(s.Results);
                var statusDisplay = BenchmarkCompletion.FormatModeStatusSummary(s.Results);
                if (!installed.Contains(s.Model))
                {
                    statusDisplay = $"{statusDisplay} · not installed";
                }

                var context = s.RecommendedCtx > 0
                    ? s.RecommendedCtx
                    : ProfileStoreService.GetRecommendedBenchmarkNumCtx(s.SizeGB);
                CatalogMetadataLookup.TryGetForModel(catalogIndex, s.Model, out var catalogEntry);
                var installedSize = s.SizeGB > 0 ? ModelSizeFormatter.FormatGb(s.SizeGB) : null;

                var contextDisplay = CatalogMetadataLookup.ResolveContext(catalogEntry, context);
                return new TestResultRowViewModel
                {
                    Model = s.Model,
                    Category = category,
                    SizeUsage = CatalogMetadataLookup.ResolveSizeUsage(catalogEntry, installedSize),
                    RecommendedCtx = context,
                    ContextSortKey = CatalogMetadataLookup.ResolveContextSortKey(catalogEntry, context),
                    ContextDisplay = contextDisplay,
                    InputModalities = CatalogMetadataLookup.ResolveInput(catalogEntry),
                    MetricSortKey = CatalogMetadataLookup.ResolveMetricSortKey(
                        benchmarkKind, s.BestTps, s.BestEmbedMs),
                    FileSizeSortKey = s.SizeGB > 0
                        ? (long)(s.SizeGB * 1_073_741_824.0)
                        : long.MaxValue,
                    BenchmarkKind = benchmarkKind,
                    BestMode = allFailed ? "FAILED" : (s.BestMode ?? string.Empty),
                    BestTps = s.BestTps,
                    BestEmbedMs = s.BestEmbedMs,
                    CpuResult = FormatModeResult(s.Results, "CPU", isEmbed),
                    ApuResult = FormatModeResult(s.Results, "APU", isEmbed),
                    GpuResult = FormatModeResult(s.Results, "GPU", isEmbed),
                    HybridResult = FormatModeResult(s.Results, "Hybrid", isEmbed),
                    CpuFailed = IsModeFailed(s.Results, "CPU"),
                    ApuFailed = IsModeFailed(s.Results, "APU"),
                    GpuFailed = IsModeFailed(s.Results, "GPU"),
                    HybridFailed = IsModeFailed(s.Results, "Hybrid"),
                    IsInstalledLocally = installed.Contains(s.Model),
                    StatusDisplay = statusDisplay,
                    IsAllModesFailed = allFailed,
                    Insight = string.Empty,
                    LastTested = s.LastTested
                };
            })
            .OrderByDescending(r => r.LastTested)
            .ToList();
    }

    private static bool IsModeFailed(Dictionary<string, ModeResultEntry>? results, string mode)
    {
        if (results is null || !results.TryGetValue(mode, out var row))
        {
            return false;
        }

        return row.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
               || row.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatModeResult(
        Dictionary<string, ModeResultEntry>? results,
        string mode,
        bool isEmbed)
    {
        if (results is null || !results.TryGetValue(mode, out var row))
        {
            return "-";
        }

        if (IsModeFailed(results, mode))
        {
            return "FAIL";
        }

        if (isEmbed)
        {
            return row.EmbedLatencyMs > 0 ? $"{row.EmbedLatencyMs:F1}ms" : row.Status;
        }

        return row.GenerationTps > 0 ? $"{row.GenerationTps:F1}" : row.Status;
    }

    private static Dictionary<string, string> BuildInstalledFileSizeMap(IReadOnlyList<OllamaModelTag> tags)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in tags.GroupBy(t => ModelInstallMatcher.LibraryName(t.Name), StringComparer.OrdinalIgnoreCase))
        {
            var sizes = group.Where(t => t.Size > 0).Select(t => t.Size).ToList();
            if (sizes.Count == 0)
            {
                continue;
            }

            var min = sizes.Min();
            var max = sizes.Max();
            map[group.Key] = min == max
                ? ModelSizeFormatter.FormatBytes(min)
                : $"{ModelSizeFormatter.FormatBytes(min)}-{ModelSizeFormatter.FormatBytes(max)}";
        }

        return map;
    }

    public async Task<HashSet<string>> GetInstalledModelNamesAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _apiClient.FetchTagsSnapshotAsync(forceRefresh: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Reachable
            ? snapshot.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(bool Reachable, HashSet<string> Installed)> GetInstalledNameSetAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _apiClient.FetchTagsSnapshotAsync(forceRefresh: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Reachable
            ? (true, snapshot.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase))
            : (false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<UndownloadTestCandidate>> GetUndownloadTestQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _catalogStore.GetEntriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var (reachable, installed) = await GetInstalledNameSetAsync(cancellationToken).ConfigureAwait(false);
        if (!reachable)
        {
            return Array.Empty<UndownloadTestCandidate>();
        }

        var profileDoc = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(false);

        var candidates = new List<UndownloadTestCandidate>();
        foreach (var entry in entries)
        {
            var libraryName = CatalogEntryNames.LibraryName(entry);
            var pullTag = CatalogEntryNames.PullTag(entry);
            var isInstalled = installed.Contains(pullTag) || installed.Contains(entry.Name);
            if (isInstalled)
            {
                continue;
            }

            var profile = ProfileResolver.ResolveForLibrary(pullTag, profileDoc)
                ?? ProfileResolver.ResolveForLibrary(libraryName, profileDoc);
            if (!BenchmarkCompletion.ShouldRetest(profile))
            {
                continue;
            }

            var (bytes, hasKnownFileSize) = CatalogFileSizeResolver.GetSortBytesWithKnown(entry);

            candidates.Add(new UndownloadTestCandidate
            {
                LibraryName = libraryName,
                PullTag = pullTag,
                FileSizeBytes = bytes,
                HasKnownFileSize = hasKnownFileSize
            });
        }

        return SortUndownloadCandidates(candidates);
    }

    private static (string BestMode, string BestTps, string BenchmarkKind, double BestTpsValue, double BestEmbedMs)
        ResolveCatalogBenchmarkMetrics(string modelOrLibrary, ModelProfileStoreDocument profileDoc)
    {
        var profile = ProfileResolver.ResolveForLibrary(modelOrLibrary, profileDoc);
        if (profile is null
            || string.IsNullOrEmpty(profile.BestMode)
            || !ProfileSanitizer.IsSupportedMode(profile.BestMode))
        {
            return ("-", "-", BenchmarkKinds.Generate, 0, 0);
        }

        var (bestMode, bestTps) = ResolveCatalogBenchmarkDisplay(modelOrLibrary, profileDoc);
        var benchmarkKind = string.IsNullOrWhiteSpace(profile.BenchmarkKind)
            ? BenchmarkKinds.Generate
            : profile.BenchmarkKind;

        return (bestMode, bestTps, benchmarkKind, profile.BestTps, profile.BestEmbedMs);
    }

    private static (string BestMode, string BestTps) ResolveCatalogBenchmarkDisplay(
        string libraryName,
        ModelProfileStoreDocument profileDoc)
    {
        ModelProfileEntry? profile = null;
        if (profileDoc.Models.TryGetValue(libraryName, out var direct))
        {
            profile = direct;
        }
        else
        {
            foreach (var (key, entry) in profileDoc.Models)
            {
                if (!string.IsNullOrEmpty(entry.BestMode)
                    && (key.Equals(libraryName, StringComparison.OrdinalIgnoreCase)
                        || key.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase)))
                {
                    profile = entry;
                    break;
                }
            }
        }

        if (profile is null
            || string.IsNullOrEmpty(profile.BestMode)
            || !ProfileSanitizer.IsSupportedMode(profile.BestMode))
        {
            return ("-", "-");
        }

        if (profile.BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase))
        {
            return (profile.BestMode, profile.BestEmbedMs > 0 ? $"{profile.BestEmbedMs:F1} ms" : "-");
        }

        return (profile.BestMode, profile.BestTps > 0 ? $"{profile.BestTps:F1}" : "-");
    }

    internal static IReadOnlyList<UndownloadTestCandidate> SortUndownloadCandidates(
        IReadOnlyList<UndownloadTestCandidate> candidates) =>
        candidates
            .OrderBy(c => c.HasKnownFileSize ? 0 : 1)
            .ThenBy(c => c.FileSizeBytes)
            .ThenBy(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int ResolveBenchmarkContext(string modelOrLibrary, ModelProfileStoreDocument profileDoc) =>
        ProfileResolver.ResolveForLibrary(modelOrLibrary, profileDoc) is { NumCtx: > 0 } profile
            ? profile.NumCtx
            : 0;

    private static (string BenchmarkKind, long MetricSortKey) ResolveCatalogMetricSort(
        string modelOrLibrary,
        ModelProfileStoreDocument profileDoc)
    {
        var profile = ProfileResolver.ResolveForLibrary(modelOrLibrary, profileDoc);
        if (profile is null)
        {
            return (BenchmarkKinds.Generate, 0);
        }

        var kind = string.IsNullOrWhiteSpace(profile.BenchmarkKind)
            ? BenchmarkKinds.Generate
            : profile.BenchmarkKind;
        return (kind, CatalogMetadataLookup.ResolveMetricSortKey(kind, profile.BestTps, profile.BestEmbedMs));
    }
}