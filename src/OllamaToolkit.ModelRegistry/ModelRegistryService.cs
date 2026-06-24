using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
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
        CancellationToken cancellationToken = default)
    {
        var entries = await _catalogStore.GetEntriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var categoryDoc = await _categories.LoadAsync(cancellationToken).ConfigureAwait(false);
        var installed = await GetInstalledNameSetAsync(cancellationToken).ConfigureAwait(false);

        var rows = entries.Select(e =>
        {
            var category = e.Category;
            if (string.IsNullOrEmpty(category)
                && categoryDoc.Models.TryGetValue(e.Name, out var catEntry))
            {
                category = catEntry.Category;
            }

            category ??= CategoryNormalizer.HeuristicCategory(e.Name, e.Description, e.Tags);
            var listDesc = !string.IsNullOrEmpty(e.ListDescription)
                ? e.ListDescription
                : DescriptionStoreService.TruncateListDescription(e.Description);

            return new CatalogRowViewModel
            {
                Name = e.Name,
                Description = e.Description,
                ListDescription = listDesc,
                Category = category,
                ParameterSize = e.ParameterSize,
                FileSize = e.FileSize,
                Tags = e.Tags,
                Installed = installed.Contains(e.Name) || installed.Any(n => n.StartsWith($"{e.Name}:", StringComparison.OrdinalIgnoreCase)),
                SortOrder = CategoryNormalizer.GetSortOrder(category)
            };
        }).ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
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

                return new TestResultRowViewModel
                {
                    Model = s.Model,
                    Category = category,
                    BestMode = s.BestMode,
                    BestTps = s.BestTps,
                    CpuResult = FormatModeResult(s.Results, "CPU"),
                    ApuResult = FormatModeResult(s.Results, "APU"),
                    GpuResult = FormatModeResult(s.Results, "GPU"),
                    HybridResult = FormatModeResult(s.Results, "Hybrid"),
                    Insight = string.Empty,
                    LastTested = s.LastTested
                };
            })
            .OrderByDescending(r => r.LastTested)
            .ToList();
    }

    private static string FormatModeResult(Dictionary<string, ModeResultEntry>? results, string mode)
    {
        if (results is null || !results.TryGetValue(mode, out var row))
        {
            return "-";
        }

        if (row.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return "FAIL";
        }

        return row.GenerationTps > 0 ? $"{row.GenerationTps:F1}" : row.Status;
    }

    private async Task<HashSet<string>> GetInstalledNameSetAsync(CancellationToken cancellationToken)
    {
        var tags = await _apiClient.GetTagsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}