using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
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
        MigrateFileSizeFields(_cache);
        return _cache;
    }

    private static void MigrateFileSizeFields(LibraryCatalogStoreDocument store)
    {
        foreach (var entry in store.Items)
        {
            if (entry.FileSizeConfirmed)
            {
                if (entry.FileSizeBytes <= 0
                    && ModelSizeFormatter.TryParseSizeLabelToBytes(entry.FileSize, out var parsed))
                {
                    entry.FileSizeBytes = parsed;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.FileSize) && entry.FileSize != "-")
            {
                if (string.IsNullOrWhiteSpace(entry.EstimatedFileSize) || entry.EstimatedFileSize == "-")
                {
                    entry.EstimatedFileSize = entry.FileSize;
                }
            }
        }
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

                item.EstimatedFileSize = previous.EstimatedFileSize;
                item.FileSizeBytes = previous.FileSizeBytes;
                item.FileSizeConfirmed = previous.FileSizeConfirmed;
                item.FileSizeConfirmedAt = previous.FileSizeConfirmedAt;
                if (previous.FileSizeConfirmed)
                {
                    item.FileSize = previous.FileSize;
                }
                else if (!string.IsNullOrWhiteSpace(previous.EstimatedFileSize) && previous.EstimatedFileSize != "-")
                {
                    item.FileSize = previous.EstimatedFileSize;
                }
                else if (!string.IsNullOrWhiteSpace(previous.FileSize) && previous.FileSize != "-")
                {
                    item.FileSize = previous.FileSize;
                    item.EstimatedFileSize = previous.FileSize;
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

    public async Task<CatalogMergeResult> MergeSearchResultsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var url = OllamaSearchHtmlParser.BuildSearchUrl(query);
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var discovered = OllamaSearchHtmlParser.ParseSearchHtml(html);
        return await MergeDiscoveredEntriesAsync(discovered, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CatalogMergeResult> DiscoverFromSearchTermsAsync(
        IReadOnlyList<string> queries,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allDiscovered = new List<LibraryCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Searching ollama.com for \"{query}\"...");
            var url = OllamaSearchHtmlParser.BuildSearchUrl(query);
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in OllamaSearchHtmlParser.ParseSearchHtml(html))
            {
                if (seen.Add(entry.Name))
                {
                    allDiscovered.Add(entry);
                }
            }
        }

        return await MergeDiscoveredEntriesAsync(allDiscovered, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogMergeResult> MergeDiscoveredEntriesAsync(
        IReadOnlyList<LibraryCatalogEntry> discovered,
        CancellationToken cancellationToken)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = store.Items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var entry in discovered)
        {
            if (!existing.Add(entry.Name))
            {
                continue;
            }

            store.Items.Add(entry);
            added.Add(entry.Name);
        }

        if (added.Count > 0)
        {
            await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        }

        ClearCache();
        return new CatalogMergeResult(added, store.Items.Count);
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
        || string.IsNullOrWhiteSpace(entry.EstimatedFileSize)
        || entry.EstimatedFileSize == "-"
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

        if (resolution.IsCloudOnly)
        {
            entry.EstimatedFileSize = "Cloud";
            if (!entry.FileSizeConfirmed)
            {
                entry.FileSize = "Cloud";
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(resolution.FileSize) && resolution.FileSize != "-")
        {
            entry.EstimatedFileSize = resolution.FileSize;
            if (!entry.FileSizeConfirmed)
            {
                entry.FileSize = resolution.FileSize;
            }
        }
    }

    public static void ApplyConfirmedFileSize(LibraryCatalogEntry entry, long bytes, string source)
    {
        if (bytes <= 0)
        {
            return;
        }

        entry.FileSizeBytes = bytes;
        entry.FileSizeConfirmed = true;
        entry.FileSizeConfirmedAt = DateTimeOffset.UtcNow.ToString("o");
        entry.FileSize = ModelSizeFormatter.FormatBytes(bytes);
        _ = source;
    }

    public async Task<bool> ApplyConfirmedFileSizeForLibraryAsync(
        string libraryName,
        long bytes,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (bytes <= 0 || string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var entry = store.Items.FirstOrDefault(e => e.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        ApplyConfirmedFileSize(entry, bytes, source);
        await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public int CountMissingFileSizes()
    {
        if (_cache?.Items is null)
        {
            return 0;
        }

        return _cache.Items.Count(e => string.IsNullOrWhiteSpace(e.FileSize) || e.FileSize == "-");
    }

    public Task<FileSizeProbeResult> ProbeAllDownloadableFileSizesAsync(
        OllamaApiClient apiClient,
        IProgress<string>? progress = null,
        Func<FileSizeProbeResult, CancellationToken, Task>? onModelProbed = null,
        CancellationToken cancellationToken = default) =>
        ProbeAllDownloadableFileSizesCoreAsync(apiClient, progress, onModelProbed, cancellationToken);

    private async Task<FileSizeProbeResult> ProbeAllDownloadableFileSizesCoreAsync(
        OllamaApiClient apiClient,
        IProgress<string>? progress,
        Func<FileSizeProbeResult, CancellationToken, Task>? onModelProbed,
        CancellationToken cancellationToken)
    {
        CancelFileSizeEnrichment();
        _enrichCts?.Dispose();
        _enrichCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enrichToken = _enrichCts.Token;

        if (!await _enrichGate.WaitAsync(0, enrichToken).ConfigureAwait(false))
        {
            return new FileSizeProbeResult();
        }

        try
        {
            var store = await LoadAsync(enrichToken).ConfigureAwait(false);
            var targets = store.Items
                .Where(e => !e.IsCloudOnly)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var probed = 0;
            var failed = 0;
            var skippedCloud = store.Items.Count(e => e.IsCloudOnly);
            var skippedUnresolved = 0;

            for (var i = 0; i < targets.Count; i++)
            {
                enrichToken.ThrowIfCancellationRequested();
                var entry = targets[i];
                progress?.Report($"Downloading {entry.Name} ({i + 1}/{targets.Count}) to read size…");

                try
                {
                    var resolution = !string.IsNullOrWhiteSpace(entry.DefaultPullTag)
                        ? CatalogPullTagResolver.ResolveFromEntry(entry)
                        : CatalogPullTagResolver.Resolve(
                            entry.Name,
                            await FetchTagsAsync(entry.Name, enrichToken).ConfigureAwait(false));

                    if (!resolution.Resolved)
                    {
                        skippedUnresolved++;
                        failed++;
                        continue;
                    }

                    if (resolution.IsCloudOnly)
                    {
                        entry.IsCloudOnly = true;
                        entry.EstimatedFileSize = "Cloud";
                        if (!entry.FileSizeConfirmed)
                        {
                            entry.FileSize = "Cloud";
                        }

                        skippedCloud++;
                        await SaveAsync(store, enrichToken).ConfigureAwait(false);
                        continue;
                    }

                    entry.DefaultPullTag = resolution.PullTag;
                    ApplyResolution(entry, resolution);

                    var totalBytes = await apiClient.ProbePullSizeAsync(resolution.PullTag, enrichToken)
                        .ConfigureAwait(false);
                    if (totalBytes is > 0)
                    {
                        ApplyConfirmedFileSize(entry, totalBytes.Value, "pull-probe");
                        probed++;
                        progress?.Report(
                            $"Captured {ModelSizeFormatter.FormatBytes(totalBytes.Value)} for {entry.Name} — stopping");
                        await SaveAsync(store, enrichToken).ConfigureAwait(false);
                        if (onModelProbed is not null)
                        {
                            await onModelProbed(
                                new FileSizeProbeResult
                                {
                                    Probed = probed,
                                    Failed = failed,
                                    SkippedCloud = skippedCloud,
                                    SkippedUnresolved = skippedUnresolved
                                },
                                enrichToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (OperationCanceledException) when (enrichToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    failed++;
                }
            }

            return new FileSizeProbeResult
            {
                Probed = probed,
                Failed = failed,
                SkippedCloud = skippedCloud,
                SkippedUnresolved = skippedUnresolved
            };
        }
        finally
        {
            _enrichGate.Release();
        }
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
                FileSize = CatalogFileSizeDisplay.GetDisplayLabel(entry),
                Description = entry.Description,
                ParameterSize = entry.ParameterSize
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsModelInstalled(string libraryName, IReadOnlySet<string> installedModelNames) =>
        OllamaToolkit.Core.Ollama.ModelInstallMatcher.IsLibraryInstalled(libraryName, installedModelNames);
}