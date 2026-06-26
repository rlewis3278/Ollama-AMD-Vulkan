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

    public async Task ResetStoreAsync(CancellationToken cancellationToken = default)
    {
        ClearCache();
        if (File.Exists(ConfigPaths.ModelDescriptionsFile))
        {
            File.Delete(ConfigPaths.ModelDescriptionsFile);
        }

        await SaveAsync(new ModelDescriptionStoreDocument(), cancellationToken).ConfigureAwait(false);
    }

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
                Write a concise one-line summary of this Ollama model for a catalog list view.
                Do not truncate your answer; return the full summary text.
                Model: {entry.Name}
                Description: {entry.Description}
                Tags: {entry.Tags}
                Summary:
                """;
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 512, 8192, cancellationToken)
                .ConfigureAwait(false);
            var listDesc = NormalizeListDescription(text);

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

    public static string NormalizeListDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .Trim('"', '\'', '`');
    }

    public static string TruncateListDescription(string? text)
    {
        var normalized = NormalizeListDescription(text);
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
        IProgress<DescriptionRefreshItemProgress>? itemProgress = null,
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

        var ordered = entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var updated = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = ordered[i];
            progress?.Report($"AI descriptions {i + 1}/{ordered.Count}: {entry.Name}");
            itemProgress?.Report(new DescriptionRefreshItemProgress
            {
                ModelName = entry.Name,
                Phase = DescriptionRefreshPhase.Started
            });

            var listDescription = await GetListDescriptionAsync(entry, forceRegenerate: true, cancellationToken)
                .ConfigureAwait(false);
            itemProgress?.Report(new DescriptionRefreshItemProgress
            {
                ModelName = entry.Name,
                Phase = DescriptionRefreshPhase.Completed,
                ListDescription = listDescription
            });
            updated++;
        }

        return updated;
    }
}