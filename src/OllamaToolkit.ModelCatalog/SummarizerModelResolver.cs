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

        var localNames = tags.Select(t => t.Name).ToList();
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);

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

    public async Task<SummarizerModelChoices> GetSummarizerChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        var tags = await _apiClient
            .GetTagsAsync(timeoutSec: 30, forceRefresh: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var installed = tags
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var choices = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
}

public sealed record SummarizerModelChoices(
    IReadOnlyList<string> Models,
    IReadOnlySet<string> InstalledNames);