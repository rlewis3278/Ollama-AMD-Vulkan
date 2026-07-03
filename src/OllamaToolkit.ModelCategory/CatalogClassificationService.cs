using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCatalog.Models;
using OllamaToolkit.ModelCategory.Models;

namespace OllamaToolkit.ModelCategory;

public sealed class CatalogClassificationService
{
    private static int ComputeBatchSize(int totalCount) =>
        totalCount switch
        {
            <= 15 => Math.Max(5, totalCount),
            <= 60 => 15,
            <= 200 => 12,
            <= 600 => 10,
            _ => 8
        };

    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private readonly UsageCategoryStoreService _categoryStore;
    private readonly LibraryCatalogStoreService _catalogStore;

    public CatalogClassificationService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        UsageCategoryStoreService? categoryStore = null,
        LibraryCatalogStoreService? catalogStore = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _categoryStore = categoryStore ?? new UsageCategoryStoreService();
        _catalogStore = catalogStore ?? new LibraryCatalogStoreService();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task ResetAllCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await _categoryStore.ResetStoreAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await _catalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in catalog.Items)
        {
            item.Category = string.Empty;
            item.SortOrder = 0;
        }

        await _catalogStore.SaveAsync(catalog, cancellationToken).ConfigureAwait(false);
        _categoryStore.ClearCache();
        _catalogStore.ClearCache();
    }

    public async Task<int> ClassifyAllAsync(
        bool recategorize = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _settings.IsFeatureEnabledAsync("CatalogCategorization", cancellationToken)
                .ConfigureAwait(false))
        {
            return 0;
        }

        var catalog = await _catalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var categoryDoc = await _categoryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var toClassify = catalog.Items
            .GroupBy(CatalogEntryNames.LibraryName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(e => recategorize
                || !categoryDoc.Models.TryGetValue(CatalogEntryNames.LibraryName(e), out var existing)
                || !existing.AiClassified)
            .ToList();

        if (toClassify.Count == 0)
        {
            return 0;
        }

        var classified = 0;
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            progress?.Report("AI unavailable — applying heuristic categories...");
            for (var i = 0; i < toClassify.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = toClassify[i];
                progress?.Report($"Heuristic classify {i + 1}/{toClassify.Count}: {entry.Name}");
                var libraryName = CatalogEntryNames.LibraryName(entry);
                var major = CategoryNormalizer.HeuristicCategory(libraryName, entry.Description, entry.Tags);
                var sub = CategorySubcategoryCatalog.HeuristicSubcategory(
                    major, libraryName, entry.Description, entry.Tags);
                ApplyCategory(categoryDoc, entry, major, sub, null, heuristic: true);
                MergeCatalogEntry(catalog, libraryName, major, sub);
                classified++;
            }
        }
        else
        {
            var batchSize = ComputeBatchSize(toClassify.Count);
            for (var i = 0; i < toClassify.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = toClassify.Skip(i).Take(batchSize).ToList();
                progress?.Report(
                    $"Classifying batch {(i / batchSize) + 1} ({batch.Count} models, batch size {batchSize})...");

                var results = await ClassifyBatchAsync(batch, summarizer, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in batch)
                {
                    var libraryName = CatalogEntryNames.LibraryName(entry);
                    if (!results.TryGetValue(libraryName, out var result))
                    {
                        var major = CategoryNormalizer.HeuristicCategory(
                            libraryName, entry.Description, entry.Tags);
                        result = new ClassificationResult(
                            major,
                            CategorySubcategoryCatalog.HeuristicSubcategory(
                                major, libraryName, entry.Description, entry.Tags));
                    }

                    ApplyCategory(categoryDoc, entry, result.Major, result.Subcategory, summarizer, heuristic: false);
                    MergeCatalogEntry(catalog, libraryName, result.Major, result.Subcategory);
                    classified++;
                }
            }
        }

        await _categoryStore.SaveAsync(categoryDoc, cancellationToken).ConfigureAwait(false);
        await _catalogStore.SaveAsync(catalog, cancellationToken).ConfigureAwait(false);
        return classified;
    }

    public async Task<int> ClassifySelectedAsync(
        IReadOnlyList<string> libraryNames,
        bool recategorize = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (libraryNames.Count == 0)
        {
            return 0;
        }

        if (!await _settings.IsFeatureEnabledAsync("CatalogCategorization", cancellationToken)
                .ConfigureAwait(false))
        {
            return 0;
        }

        var catalog = await _catalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var categoryDoc = await _categoryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var nameSet = libraryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toClassify = catalog.Items
            .Where(e => nameSet.Contains(e.Name)
                || nameSet.Contains(CatalogEntryNames.LibraryName(e))
                || nameSet.Contains(CatalogEntryNames.PullTag(e)))
            .GroupBy(CatalogEntryNames.LibraryName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(e => recategorize
                || !categoryDoc.Models.TryGetValue(CatalogEntryNames.LibraryName(e), out var existing)
                || !existing.AiClassified)
            .ToList();

        if (toClassify.Count == 0)
        {
            return 0;
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var classified = 0;
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            progress?.Report("AI unavailable — applying heuristic categories...");
            for (var i = 0; i < toClassify.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = toClassify[i];
                progress?.Report($"Heuristic classify {i + 1}/{toClassify.Count}: {entry.Name}");
                var libraryName = CatalogEntryNames.LibraryName(entry);
                var major = CategoryNormalizer.HeuristicCategory(libraryName, entry.Description, entry.Tags);
                var sub = CategorySubcategoryCatalog.HeuristicSubcategory(
                    major, libraryName, entry.Description, entry.Tags);
                ApplyCategory(categoryDoc, entry, major, sub, null, heuristic: true);
                MergeCatalogEntry(catalog, libraryName, major, sub);
                classified++;
            }
        }
        else
        {
            var batchSize = ComputeBatchSize(toClassify.Count);
            for (var i = 0; i < toClassify.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = toClassify.Skip(i).Take(batchSize).ToList();
                progress?.Report(
                    $"Classifying batch {(i / batchSize) + 1} ({batch.Count} models, batch size {batchSize})...");

                var results = await ClassifyBatchAsync(batch, summarizer, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in batch)
                {
                    var libraryName = CatalogEntryNames.LibraryName(entry);
                    if (!results.TryGetValue(libraryName, out var result))
                    {
                        var major = CategoryNormalizer.HeuristicCategory(
                            libraryName, entry.Description, entry.Tags);
                        result = new ClassificationResult(
                            major,
                            CategorySubcategoryCatalog.HeuristicSubcategory(
                                major, libraryName, entry.Description, entry.Tags));
                    }

                    ApplyCategory(categoryDoc, entry, result.Major, result.Subcategory, summarizer, heuristic: false);
                    MergeCatalogEntry(catalog, libraryName, result.Major, result.Subcategory);
                    classified++;
                }
            }
        }

        await _categoryStore.SaveAsync(categoryDoc, cancellationToken).ConfigureAwait(false);
        await _catalogStore.SaveAsync(catalog, cancellationToken).ConfigureAwait(false);
        return classified;
    }

    private sealed record ClassificationResult(string Major, string Subcategory);

    private async Task<Dictionary<string, ClassificationResult>> ClassifyBatchAsync(
        IReadOnlyList<LibraryCatalogEntry> batch,
        string summarizer,
        CancellationToken cancellationToken)
    {
        var lines = batch.Select(e =>
            $"{CatalogEntryNames.LibraryName(e)}|{e.Description}|{e.Tags}");
        var prompt = $"""
            Classify each Ollama model into a major category AND a specific subcategory.

            Major categories:
            General Chat, Coding, Reasoning, Vision, Multimodal, Embedding, Tools, Domain Specific, Other

            Subcategories by major:
            {CategorySubcategoryCatalog.FormatListForPrompt()}

            Reply with one line per model: modelname|MajorCategory|Subcategory
            Models:
            {string.Join(Environment.NewLine, lines)}
            """;

        try
        {
            var response = await _apiClient.GenerateAsync(summarizer, prompt, 256, 8192, cancellationToken)
                .ConfigureAwait(false);
            return ParseBatchResponse(response, batch);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return batch.ToDictionary(
                CatalogEntryNames.LibraryName,
                e =>
                {
                    var libraryName = CatalogEntryNames.LibraryName(e);
                    var major = CategoryNormalizer.HeuristicCategory(libraryName, e.Description, e.Tags);
                    return new ClassificationResult(
                        major,
                        CategorySubcategoryCatalog.HeuristicSubcategory(
                            major, libraryName, e.Description, e.Tags));
                },
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, ClassificationResult> ParseBatchResponse(
        string response,
        IReadOnlyList<LibraryCatalogEntry> batch)
    {
        var map = new Dictionary<string, ClassificationResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0].Trim().TrimStart('-', '*', ' ', '\t');
            var entry = ResolveBatchEntry(name, batch);
            if (entry is null)
            {
                continue;
            }

            var major = CategoryNormalizer.Normalize(parts[1]);
            var libraryName = CatalogEntryNames.LibraryName(entry);
            var sub = parts.Length >= 3
                ? CategorySubcategoryCatalog.NormalizeSubcategory(major, parts[2])
                : CategorySubcategoryCatalog.HeuristicSubcategory(
                    major, libraryName, entry.Description, entry.Tags);
            map[libraryName] = new ClassificationResult(major, sub);
        }

        foreach (var entry in batch)
        {
            var libraryName = CatalogEntryNames.LibraryName(entry);
            if (map.ContainsKey(libraryName))
            {
                continue;
            }

            var major = CategoryNormalizer.HeuristicCategory(libraryName, entry.Description, entry.Tags);
            map[libraryName] = new ClassificationResult(
                major,
                CategorySubcategoryCatalog.HeuristicSubcategory(
                    major, libraryName, entry.Description, entry.Tags));
        }

        return map;
    }

    private static LibraryCatalogEntry? ResolveBatchEntry(
        string aiName,
        IReadOnlyList<LibraryCatalogEntry> batch)
    {
        if (string.IsNullOrWhiteSpace(aiName))
        {
            return null;
        }

        var normalized = aiName.Trim();
        foreach (var entry in batch)
        {
            if (entry.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || CatalogEntryNames.LibraryName(entry).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (var entry in batch)
        {
            var libraryName = CatalogEntryNames.LibraryName(entry);
            if (normalized.StartsWith(libraryName, StringComparison.OrdinalIgnoreCase)
                || libraryName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static void ApplyCategory(
        UsageCategoryStoreDocument doc,
        LibraryCatalogEntry entry,
        string majorCategory,
        string subcategory,
        string? summarizer,
        bool heuristic)
    {
        var libraryName = CatalogEntryNames.LibraryName(entry);
        doc.Models[libraryName] = new UsageCategoryEntry
        {
            Category = majorCategory,
            Subcategory = subcategory,
            UsageSummary = entry.Description,
            LibraryName = libraryName,
            FetchedAt = DateTimeOffset.Now.ToString("o"),
            SummaryModel = summarizer,
            AiClassified = !heuristic
        };
    }

    private static void MergeCatalogEntry(
        LibraryCatalogStoreDocument catalog,
        string name,
        string majorCategory,
        string subcategory)
    {
        foreach (var item in catalog.Items.Where(i =>
                     CatalogEntryNames.LibraryName(i).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            item.Category = CategoryNormalizer.FormatDisplay(majorCategory, subcategory);
            item.SortOrder = CategoryNormalizer.GetSortOrder(majorCategory);
        }
    }
}