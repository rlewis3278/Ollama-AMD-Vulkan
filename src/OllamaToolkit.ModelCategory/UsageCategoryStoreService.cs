using OllamaToolkit.Core;
using OllamaToolkit.ModelCategory.Models;

namespace OllamaToolkit.ModelCategory;

public sealed class UsageCategoryStoreService
{
    private UsageCategoryStoreDocument? _cache;

    public void ClearCache() => _cache = null;

    public async Task<UsageCategoryStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<UsageCategoryStoreDocument>(
            ConfigPaths.ModelUsageCategoriesFile, cancellationToken).ConfigureAwait(false)
            ?? new UsageCategoryStoreDocument();

        _cache.Models ??= new Dictionary<string, UsageCategoryEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    public async Task SaveAsync(UsageCategoryStoreDocument store, CancellationToken cancellationToken = default)
    {
        store.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = store;
        await JsonFileHelper.WriteAsync(ConfigPaths.ModelUsageCategoriesFile, store, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(int Classified, int Total)> GetStatusAsync(
        int catalogCount,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var classified = store.Models.Values.Count(e => e.AiClassified && !string.IsNullOrEmpty(e.Category));
        return (classified, catalogCount);
    }
}