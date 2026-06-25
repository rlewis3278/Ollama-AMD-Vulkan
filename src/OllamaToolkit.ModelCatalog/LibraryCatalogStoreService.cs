using OllamaToolkit.Core;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public sealed class LibraryCatalogStoreService
{
    private const string CatalogUrl = "https://ollama.com/library";
    private static readonly TimeSpan StoreMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient _httpClient;
    private LibraryCatalogStoreDocument? _cache;

    public LibraryCatalogStoreService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void ClearCache() => _cache = null;

    public async Task<LibraryCatalogStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<LibraryCatalogStoreDocument>(
            ConfigPaths.LibraryCatalogStoreFile, cancellationToken).ConfigureAwait(false)
            ?? new LibraryCatalogStoreDocument();

        _cache.Items ??= new List<LibraryCatalogEntry>();
        return _cache;
    }

    public async Task SaveAsync(LibraryCatalogStoreDocument store, CancellationToken cancellationToken = default)
    {
        store.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = store;
        await JsonFileHelper.WriteAsync(ConfigPaths.LibraryCatalogStoreFile, store, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsStaleAsync(CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (store.Items.Count == 0 || string.IsNullOrEmpty(store.CatalogFetchedAt))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(store.CatalogFetchedAt, out var fetched))
        {
            return true;
        }

        return DateTimeOffset.UtcNow - fetched > StoreMaxAge;
    }

    public async Task<IReadOnlyList<LibraryCatalogEntry>> RefreshFromWebAsync(
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var url = CatalogUrl;
        if (!string.IsNullOrWhiteSpace(search))
        {
            url = $"{CatalogUrl}?q={Uri.EscapeDataString(search.Trim())}";
        }

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var items = OllamaLibraryHtmlParser.ParseListingHtml(html).ToList();

        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(search))
        {
            store.Items = items;
            store.CatalogFetchedAt = DateTimeOffset.Now.ToString("o");
        }

        await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        return items;
    }

    public async Task<IReadOnlyList<LibraryCatalogEntry>> GetEntriesAsync(
        bool refreshIfStale = false,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (refreshIfStale && await IsStaleAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshFromWebAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return store.Items;
    }

    public async Task<int> EnrichFileSizesAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var missing = store.Items
            .Where(e => string.IsNullOrWhiteSpace(e.FileSize) || e.FileSize == "-")
            .ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        var enriched = 0;
        using var gate = new SemaphoreSlim(4);
        var completed = 0;
        var tasks = missing.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = $"{CatalogUrl}/{entry.Name}";
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var size = OllamaLibraryDetailParser.ParseFileSizeRange(html);
                if (size != "-" && !string.IsNullOrWhiteSpace(size))
                {
                    entry.FileSize = size;
                    Interlocked.Increment(ref enriched);
                }
            }
            catch
            {
                // Best-effort enrichment; skip models that fail to load.
            }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == missing.Count)
                {
                    progress?.Report($"Fetching catalog file sizes ({done}/{missing.Count})...");
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (enriched > 0)
        {
            await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        }

        return enriched;
    }

    public async Task ProcessCatalogEntriesUiPassAsync(
        IReadOnlySet<string> installedModelNames,
        IProgress<string>? progress,
        Func<CatalogRefreshItemProgress, CancellationToken, Task> onItemProgress,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var items = store.Items;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = items[i];
            var isInstalled = IsModelInstalled(entry.Name, installedModelNames);

            progress?.Report($"Refreshing catalog {i + 1}/{items.Count}: {entry.Name}");

            await onItemProgress(new CatalogRefreshItemProgress
            {
                ModelName = entry.Name,
                Phase = CatalogRefreshPhase.Started,
                IsInstalled = isInstalled
            }, cancellationToken).ConfigureAwait(false);

            await onItemProgress(new CatalogRefreshItemProgress
            {
                ModelName = entry.Name,
                Phase = CatalogRefreshPhase.Completed,
                IsInstalled = isInstalled,
                FileSize = entry.FileSize,
                Description = entry.Description,
                ParameterSize = entry.ParameterSize
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsModelInstalled(string libraryName, IReadOnlySet<string> installedModelNames) =>
        installedModelNames.Contains(libraryName)
        || installedModelNames.Any(n => n.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase));
}