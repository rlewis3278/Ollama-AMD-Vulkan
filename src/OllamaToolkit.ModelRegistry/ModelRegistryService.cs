using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCatalog.Models;
using OllamaToolkit.ModelCategory;
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
            string category;
            if (categoryDoc.Models.TryGetValue(e.Name, out var catEntry)
                && !string.IsNullOrWhiteSpace(catEntry.Category))
            {
                category = catEntry.Category;
            }
            else if (!string.IsNullOrWhiteSpace(e.Category))
            {
                category = e.Category;
            }
            else
            {
                category = CategoryNormalizer.HeuristicCategory(e.Name, e.Description, e.Tags);
            }
            var downloadDescription = string.IsNullOrWhiteSpace(e.Description)
                ? string.Empty
                : e.Description.Trim();
            var aiDescription = descriptionDoc.Models.TryGetValue(e.Name, out var aiEntry)
                && !string.IsNullOrWhiteSpace(aiEntry.ListDescription)
                ? aiEntry.ListDescription!.Trim()
                : string.Empty;
            var listDesc = !string.IsNullOrEmpty(e.ListDescription)
                ? e.ListDescription
                : DescriptionStoreService.NormalizeListDescription(e.Description);
            var displayDescription = descriptionMode == CatalogDescriptionDisplayMode.Ai
                ? (string.IsNullOrWhiteSpace(aiDescription) ? "(not summarized)" : aiDescription)
                : downloadDescription;

            var fileSize = e.FileSize;
            if ((fileSize == "-" || string.IsNullOrWhiteSpace(fileSize)) && e.IsCloudOnly)
            {
                fileSize = "Cloud";
            }
            else if ((fileSize == "-" || string.IsNullOrWhiteSpace(fileSize))
                && installedSizes.TryGetValue(e.Name, out var installedSize))
            {
                fileSize = installedSize;
            }

            var isInstalled = tagsSnapshot.Reachable
                && ModelInstallMatcher.IsLibraryInstalled(e.Name, installed);
            var (bestMode, bestTps) = ResolveCatalogBenchmarkDisplay(e.Name, profileDoc);

            rows.Add(new CatalogRowViewModel
            {
                Name = e.Name,
                DefaultPullTag = e.DefaultPullTag,
                IsCloudOnly = e.IsCloudOnly,
                Description = e.Description,
                ListDescription = listDesc,
                DownloadDescription = downloadDescription,
                AiDescription = aiDescription,
                DisplayDescription = displayDescription,
                Category = category,
                ParameterSize = e.ParameterSize,
                FileSize = ModelSizeFormatter.FormatSizeLabel(fileSize),
                Tags = e.Tags,
                Installed = isInstalled,
                InstalledDisplay = tagsSnapshot.Reachable
                    ? isInstalled ? "Yes" : "No"
                    : "Unknown",
                BestMode = bestMode,
                BestTps = bestTps,
                SortOrder = CategoryNormalizer.GetSortOrder(category)
            });
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.DownloadDescription.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.AiDescription.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.DisplayDescription.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Tags.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(categoryFilter) && categoryFilter != "All")
        {
            rows = rows.Where(r => r.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToList();
    }

    public async Task<IReadOnlyList<TestResultRowViewModel>> GetTestResultRowsAsync(
        CancellationToken cancellationToken = default)
    {
        var summaries = await _profiles.GetAllSummariesAsync(cancellationToken).ConfigureAwait(false);
        var categoryDoc = await _categories.LoadAsync(cancellationToken).ConfigureAwait(false);

        return summaries
            .Where(s => !s.NeedsRetest && !string.IsNullOrEmpty(s.BestMode))
            .Select(s =>
            {
                var category = string.Empty;
                var libName = s.Model.Split(':')[0];
                if (categoryDoc.Models.TryGetValue(libName, out var cat))
                {
                    category = cat.Category;
                }

                var isEmbed = s.BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase);
                return new TestResultRowViewModel
                {
                    Model = s.Model,
                    Category = category,
                    BenchmarkKind = s.BenchmarkKind,
                    BestMode = s.BestMode,
                    BestTps = s.BestTps,
                    BestEmbedMs = s.BestEmbedMs,
                    CpuResult = FormatModeResult(s.Results, "CPU", isEmbed),
                    ApuResult = FormatModeResult(s.Results, "APU", isEmbed),
                    GpuResult = FormatModeResult(s.Results, "GPU", isEmbed),
                    HybridResult = FormatModeResult(s.Results, "Hybrid", isEmbed),
                    Insight = string.Empty,
                    LastTested = s.LastTested
                };
            })
            .OrderByDescending(r => r.LastTested)
            .ToList();
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

        if (row.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            || row.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
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
            var isInstalled = ModelInstallMatcher.IsLibraryInstalled(entry.Name, installed);
            if (isInstalled)
            {
                continue;
            }

            if (!CatalogEntryNeedsRetest(entry.Name, profileDoc))
            {
                continue;
            }

            var hasKnownFileSize = ModelSizeFormatter.TryParseSizeLabelToBytes(entry.FileSize, out var bytes);
            if (!hasKnownFileSize)
            {
                bytes = long.MaxValue;
            }

            var pullTag = !string.IsNullOrWhiteSpace(entry.DefaultPullTag)
                ? entry.DefaultPullTag
                : $"{entry.Name}:latest";

            candidates.Add(new UndownloadTestCandidate
            {
                LibraryName = entry.Name,
                PullTag = pullTag,
                FileSizeBytes = bytes,
                HasKnownFileSize = hasKnownFileSize
            });
        }

        return SortUndownloadCandidates(candidates);
    }

    private static bool CatalogEntryNeedsRetest(string libraryName, ModelProfileStoreDocument profileDoc)
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
                if (key.Equals(libraryName, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase))
                {
                    profile = entry;
                    break;
                }
            }
        }

        return profile is null || string.IsNullOrEmpty(profile.BestMode);
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

        if (profile is null || string.IsNullOrEmpty(profile.BestMode))
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
}