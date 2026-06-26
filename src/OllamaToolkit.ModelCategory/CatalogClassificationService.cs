using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.ModelCatalog.Models;
using OllamaToolkit.ModelCategory.Models;

namespace OllamaToolkit.ModelCategory;

public sealed class CatalogClassificationService
{
    private const int BatchSize = 15;

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
            .Where(e => recategorize
                || !categoryDoc.Models.TryGetValue(e.Name, out var existing)
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
                var category = CategoryNormalizer.HeuristicCategory(entry.Name, entry.Description, entry.Tags);
                ApplyCategory(categoryDoc, entry, category, null, heuristic: true);
                MergeCatalogEntry(catalog, entry.Name, category);
                classified++;
            }
        }
        else
        {
            for (var i = 0; i < toClassify.Count; i += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = toClassify.Skip(i).Take(BatchSize).ToList();
                progress?.Report($"Classifying batch {(i / BatchSize) + 1} ({batch.Count} models)...");

                var results = await ClassifyBatchAsync(batch, summarizer, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in batch)
                {
                    if (!results.TryGetValue(entry.Name, out var category))
                    {
                        category = CategoryNormalizer.HeuristicCategory(
                            entry.Name, entry.Description, entry.Tags);
                    }

                    ApplyCategory(categoryDoc, entry, category, summarizer, heuristic: false);
                    MergeCatalogEntry(catalog, entry.Name, category);
                    classified++;
                }
            }
        }

        await _categoryStore.SaveAsync(categoryDoc, cancellationToken).ConfigureAwait(false);
        await _catalogStore.SaveAsync(catalog, cancellationToken).ConfigureAwait(false);
        return classified;
    }

    private async Task<Dictionary<string, string>> ClassifyBatchAsync(
        IReadOnlyList<LibraryCatalogEntry> batch,
        string summarizer,
        CancellationToken cancellationToken)
    {
        var lines = batch.Select(e =>
            $"{e.Name}|{e.Description}|{e.Tags}");
        var prompt = $"""
            Classify each Ollama model into exactly one category from this list:
            General Chat, Coding, Reasoning, Vision, Multimodal, Embedding, Tools, Domain Specific, Other

            Reply with one line per model: modelname|Category
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
                e => e.Name,
                e => CategoryNormalizer.HeuristicCategory(e.Name, e.Description, e.Tags),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> ParseBatchResponse(
        string response,
        IReadOnlyList<LibraryCatalogEntry> batch)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 2);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0].Trim().TrimStart('-', '*', ' ', '\t');
            var category = CategoryNormalizer.Normalize(parts[1]);
            var entry = ResolveBatchEntry(name, batch);
            if (entry is null)
            {
                continue;
            }

            map[entry.Name] = category;
        }

        foreach (var entry in batch)
        {
            if (!map.ContainsKey(entry.Name))
            {
                map[entry.Name] = CategoryNormalizer.HeuristicCategory(
                    entry.Name, entry.Description, entry.Tags);
            }
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
            if (entry.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        foreach (var entry in batch)
        {
            if (normalized.StartsWith(entry.Name, StringComparison.OrdinalIgnoreCase)
                || entry.Name.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static void ApplyCategory(
        UsageCategoryStoreDocument doc,
        LibraryCatalogEntry entry,
        string category,
        string? summarizer,
        bool heuristic)
    {
        doc.Models[entry.Name] = new UsageCategoryEntry
        {
            Category = category,
            UsageSummary = entry.Description,
            LibraryName = entry.Name,
            FetchedAt = DateTimeOffset.Now.ToString("o"),
            SummaryModel = summarizer,
            AiClassified = !heuristic
        };
    }

    private static void MergeCatalogEntry(LibraryCatalogStoreDocument catalog, string name, string category)
    {
        var item = catalog.Items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.Category = category;
        item.SortOrder = CategoryNormalizer.GetSortOrder(category);
    }
}