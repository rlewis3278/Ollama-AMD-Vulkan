using OllamaToolkit.Core;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public sealed class LibraryCatalogStoreService
{
    private const string CatalogUrl = "https://ollama.com/library";
    private static readonly TimeSpan StoreMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _enrichGate = new(1, 1);
    private CancellationTokenSource? _enrichCts;
    private LibraryCatalogStoreDocument? _cache;

    public LibraryCatalogStoreService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void ClearCache() => _cache = null;

    public async Task ResetStoreAsync(CancellationToken cancellationToken = default)
    {
        var store = new LibraryCatalogStoreDocument
        {
            Items = new List<LibraryCatalogEntry>(),
            CatalogFetchedAt = null,
            SortGeneratedAt = null,
            SummaryModel = null
        };
        await SaveAsync(store, cancellationToken).ConfigureAwait(false);
    }

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
            var preserved = store.Items.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            store.Items = items;
            foreach (var item in store.Items)
            {
                if (!preserved.TryGetValue(item.Name, out var previous))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(previous.FileSize) && previous.FileSize != "-")
                {
                    item.FileSize = previous.FileSize;
                }

                if (!string.IsNullOrWhiteSpace(previous.Category))
                {
                    item.Category = previous.Category;
                }

                if (previous.SortOrder != 0)
                {
                    item.SortOrder = previous.SortOrder;
                }

                if (!string.IsNullOrWhiteSpace(previous.ListDescription))
                {
                    item.ListDescription = previous.ListDescription;
                }
            }

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

    private static readonly TimeSpan FileSizeRequestTimeout = TimeSpan.FromSeconds(15);

    public void CancelFileSizeEnrichment()
    {
        try
        {
            _enrichCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Enrichment already finished.
        }
    }

    public async Task<int> EnrichFileSizesAsync(
        IProgress<string>? progress = null,
        Func<bool>? shouldAbort = null,
        CancellationToken cancellationToken = default)
    {
        CancelFileSizeEnrichment();
        _enrichCts?.Dispose();
        _enrichCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enrichToken = _enrichCts.Token;

        if (!await _enrichGate.WaitAsync(0, enrichToken).ConfigureAwait(false))
        {
            return 0;
        }

        try
        {
            return await EnrichFileSizesCoreAsync(progress, shouldAbort, enrichToken).ConfigureAwait(false);
        }
        finally
        {
            _enrichGate.Release();
        }
    }

    private async Task<int> EnrichFileSizesCoreAsync(
        IProgress<string>? progress,
        Func<bool>? shouldAbort,
        CancellationToken cancellationToken)
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
        for (var i = 0; i < missing.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldAbort?.Invoke() == true)
            {
                break;
            }

            var entry = missing[i];
            progress?.Report($"Fetching catalog file sizes ({i + 1}/{missing.Count}): {entry.Name}");

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(FileSizeRequestTimeout);

                var url = $"{CatalogUrl}/{entry.Name}";
                using var response = await _httpClient.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var html = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var size = OllamaLibraryDetailParser.ParseFileSizeRange(html);
                if (size != "-" && !string.IsNullOrWhiteSpace(size))
                {
                    entry.FileSize = size;
                    enriched++;
                    await SaveAsync(store, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort enrichment; skip models that fail or time out.
            }
        }

        return enriched;
    }

    public int CountMissingFileSizes()
    {
        if (_cache?.Items is null)
        {
            return 0;
        }

        return _cache.Items.Count(e => string.IsNullOrWhiteSpace(e.FileSize) || e.FileSize == "-");
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