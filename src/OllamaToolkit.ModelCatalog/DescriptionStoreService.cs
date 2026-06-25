using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public sealed class DescriptionStoreService
{
    public const int ListDescriptionMaxChars = 72;

    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private ModelDescriptionStoreDocument? _cache;

    public DescriptionStoreService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public void ClearCache() => _cache = null;

    public async Task<ModelDescriptionStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<ModelDescriptionStoreDocument>(
            ConfigPaths.ModelDescriptionsFile, cancellationToken).ConfigureAwait(false)
            ?? new ModelDescriptionStoreDocument();

        _cache.Models ??= new Dictionary<string, ModelDescriptionEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    public async Task SaveAsync(ModelDescriptionStoreDocument store, CancellationToken cancellationToken = default)
    {
        store.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = store;
        await JsonFileHelper.WriteAsync(ConfigPaths.ModelDescriptionsFile, store, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> GetListDescriptionAsync(
        LibraryCatalogEntry entry,
        bool forceRegenerate = false,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!forceRegenerate
            && store.Models.TryGetValue(entry.Name, out var cached)
            && !string.IsNullOrWhiteSpace(cached.ListDescription))
        {
            return cached.ListDescription!;
        }

        if (!await _settings.IsFeatureEnabledAsync("DescriptionSummarization", cancellationToken)
                .ConfigureAwait(false))
        {
            return TruncateListDescription(entry.Description);
        }

        var summarizer = await _summarizer.ResolveAsync(entry.Name, cancellationToken).ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return TruncateListDescription(entry.Description);
        }

        try
        {
            var prompt = $"""
                Summarize this Ollama model in at most {ListDescriptionMaxChars} characters for a list view.
                Model: {entry.Name}
                Description: {entry.Description}
                Tags: {entry.Tags}
                Summary:
                """;
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 96, 4096, cancellationToken)
                .ConfigureAwait(false);
            var listDesc = TruncateListDescription(text);

            store.Models[entry.Name] = new ModelDescriptionEntry
            {
                ShortDescription = entry.Description,
                ListDescription = listDesc,
                LibraryName = entry.Name,
                FetchedAt = DateTimeOffset.Now.ToString("o"),
                SummaryModel = summarizer,
                SummaryGeneratedAt = DateTimeOffset.Now.ToString("o")
            };
            await SaveAsync(store, cancellationToken).ConfigureAwait(false);
            return listDesc;
        }
        catch
        {
            return TruncateListDescription(entry.Description);
        }
    }

    public static string TruncateListDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        normalized = normalized.Trim('"', '\'', '`');
        if (normalized.Length <= ListDescriptionMaxChars)
        {
            return normalized;
        }

        return normalized[..(ListDescriptionMaxChars - 3)] + "...";
    }

    public async Task<string?> GetCachedListDescriptionAsync(
        string libraryName,
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return store.Models.TryGetValue(libraryName, out var cached)
            ? cached.ListDescription
            : null;
    }

    public async Task<int> RefreshAllListDescriptionsAsync(
        IReadOnlyList<LibraryCatalogEntry> entries,
        bool forceRegenerate,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (forceRegenerate)
        {
            var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                store.Models.Remove(entry.Name);
            }

            await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        }

        var updated = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            progress?.Report($"AI descriptions {i + 1}/{entries.Count}: {entry.Name}");
            await GetListDescriptionAsync(entry, forceRegenerate: true, cancellationToken).ConfigureAwait(false);
            updated++;
        }

        return updated;
    }
}