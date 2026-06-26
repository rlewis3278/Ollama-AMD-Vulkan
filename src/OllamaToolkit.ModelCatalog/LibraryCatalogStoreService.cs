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
        ClearCache();
        if (File.Exists(ConfigPaths.LibraryCatalogStoreFile))
        {
            File.Delete(ConfigPaths.LibraryCatalogStoreFile);
        }

        var store = new LibraryCatalogStoreDocument
        {
            Items = [],
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

                if (!string.IsNullOrWhiteSpace(previous.DefaultPullTag))
                {
                    item.DefaultPullTag = previous.DefaultPullTag;
                }

                item.IsCloudOnly = previous.IsCloudOnly;
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

    public Task<int> EnrichFileSizesAsync(
        IProgress<string>? progress = null,
        Func<bool>? shouldAbort = null,
        CancellationToken cancellationToken = default) =>
        EnrichTagsAndMetadataAsync(progress, shouldAbort, cancellationToken);

    public async Task<int> EnrichTagsAndMetadataAsync(
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
            return await EnrichTagsAndMetadataCoreAsync(progress, shouldAbort, enrichToken).ConfigureAwait(false);
        }
        finally
        {
            _enrichGate.Release();
        }
    }

    public async Task<CatalogPullResolution> ResolvePullTagAsync(
        string libraryName,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var entry = store.Items.FirstOrDefault(e => e.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.DefaultPullTag))
        {
            return CatalogPullTagResolver.ResolveFromEntry(entry);
        }

        var tags = await FetchTagsAsync(libraryName, cancellationToken).ConfigureAwait(false);
        var resolution = CatalogPullTagResolver.Resolve(libraryName, tags);
        if (entry is not null && resolution.Resolved)
        {
            ApplyResolution(entry, resolution);
            await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        }

        return resolution;
    }

    private async Task<int> EnrichTagsAndMetadataCoreAsync(
        IProgress<string>? progress,
        Func<bool>? shouldAbort,
        CancellationToken cancellationToken)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var missing = store.Items
            .Where(NeedsTagMetadataEnrichment)
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
            progress?.Report($"Fetching catalog tags ({i + 1}/{missing.Count}): {entry.Name}");

            try
            {
                var tags = await FetchTagsAsync(entry.Name, cancellationToken).ConfigureAwait(false);
                if (tags.Count == 0)
                {
                    continue;
                }

                var resolution = CatalogPullTagResolver.Resolve(entry.Name, tags);
                if (!resolution.Resolved)
                {
                    continue;
                }

                ApplyResolution(entry, resolution);
                enriched++;
                await SaveAsync(store, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<LibraryTagInfo>> FetchTagsAsync(
        string libraryName,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FileSizeRequestTimeout);

        var url = $"{CatalogUrl}/{libraryName}/tags";
        using var response = await _httpClient.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<LibraryTagInfo>();
        }

        var html = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return OllamaLibraryTagsParser.ParseTagsHtml(html, libraryName);
    }

    private static bool NeedsTagMetadataEnrichment(LibraryCatalogEntry entry) =>
        string.IsNullOrWhiteSpace(entry.DefaultPullTag)
        || string.IsNullOrWhiteSpace(entry.FileSize)
        || entry.FileSize == "-"
        || string.IsNullOrWhiteSpace(entry.ParameterSize)
        || entry.ParameterSize == "-";

    private static void ApplyResolution(LibraryCatalogEntry entry, CatalogPullResolution resolution)
    {
        entry.DefaultPullTag = resolution.PullTag;
        entry.IsCloudOnly = resolution.IsCloudOnly;
        if (!string.IsNullOrWhiteSpace(resolution.ParameterSize) && resolution.ParameterSize != "-")
        {
            entry.ParameterSize = resolution.ParameterSize;
        }
        else if (entry.ParameterSize is "-" or "")
        {
            entry.ParameterSize = OllamaLibraryTagsParser.TryParseParamsFromDescription(entry.Description);
        }

        if (!string.IsNullOrWhiteSpace(resolution.FileSize) && resolution.FileSize != "-")
        {
            entry.FileSize = resolution.FileSize;
        }
        else if (resolution.IsCloudOnly)
        {
            entry.FileSize = "Cloud";
        }
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
        OllamaToolkit.Core.Ollama.ModelInstallMatcher.IsLibraryInstalled(libraryName, installedModelNames);
}