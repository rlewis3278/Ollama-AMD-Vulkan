using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;

namespace OllamaToolkit.ModelCatalog;

public sealed class SummarizerModelResolver
{
    public static readonly string[] DefaultPreferenceOrder =
    [
        "llama3.2:3b",
        "phi4-mini:latest",
        "qwen2.5-coder:7b",
        "phi4-mini-reasoning:latest"
    ];

    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;

    public SummarizerModelResolver(AiSettingsService? settings = null, OllamaApiClient? apiClient = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
    }

    public async Task<string?> ResolveAsync(
        string? excludeModelName = null,
        CancellationToken cancellationToken = default)
    {
        var tags = await _apiClient.GetTagsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tags.Count == 0)
        {
            return null;
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return ResolveFromTags(tags, settings, excludeModelName);
    }

    public static string? ResolveFromTags(
        IReadOnlyList<OllamaModelTag> tags,
        AiSettingsDocument settings,
        string? excludeModelName = null)
    {
        if (tags.Count == 0)
        {
            return null;
        }

        var localNames = tags.Select(t => t.Name).ToList();

        if (!string.IsNullOrWhiteSpace(settings.PreferredSummarizerModel)
            && localNames.Contains(settings.PreferredSummarizerModel, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(settings.PreferredSummarizerModel, excludeModelName, StringComparison.OrdinalIgnoreCase))
        {
            return settings.PreferredSummarizerModel;
        }

        foreach (var preferred in DefaultPreferenceOrder)
        {
            if (string.Equals(preferred, excludeModelName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (localNames.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            {
                return preferred;
            }
        }

        var smallest = tags
            .Where(t => !string.Equals(t.Name, excludeModelName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Size)
            .FirstOrDefault();

        return smallest?.Name;
    }

    public static SummarizerModelChoices BuildChoicesFromTags(
        IReadOnlyList<OllamaModelTag> tags,
        string? ensureModel = null)
    {
        var installed = tags
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var choices = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(ensureModel) && seen.Add(ensureModel))
        {
            choices.Add(ensureModel);
        }

        foreach (var preferred in DefaultPreferenceOrder)
        {
            if (seen.Add(preferred))
            {
                choices.Add(preferred);
            }
        }

        foreach (var name in installed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(name))
            {
                choices.Add(name);
            }
        }

        return new SummarizerModelChoices(choices, installed);
    }

    public static SummarizerModelChoices BuildDefaultChoices(string? preferredModel = null) =>
        BuildChoicesFromTags(Array.Empty<OllamaModelTag>(), preferredModel);

    public async Task<SummarizerModelChoices> GetSummarizerChoicesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var tags = await _apiClient
            .GetTagsAsync(
                timeoutSec: forceRefresh ? 30 : 10,
                forceRefresh: forceRefresh,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return BuildChoicesFromTags(tags, settings.PreferredSummarizerModel);
    }

    public async Task<SummarizerUiState> FetchUiStateAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var tags = await _apiClient
            .GetTagsAsync(
                timeoutSec: forceRefresh ? 30 : 10,
                forceRefresh: forceRefresh,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var choices = BuildChoicesFromTags(tags, settings.PreferredSummarizerModel);
        var activeSummarizer = ResolveFromTags(tags, settings);
        return new SummarizerUiState(tags.Count > 0, activeSummarizer, choices, settings);
    }
}

public sealed record SummarizerModelChoices(
    IReadOnlyList<string> Models,
    IReadOnlySet<string> InstalledNames);

public sealed record SummarizerUiState(
    bool HasInstalledTags,
    string? ActiveSummarizer,
    SummarizerModelChoices Choices,
    AiSettingsDocument Settings);