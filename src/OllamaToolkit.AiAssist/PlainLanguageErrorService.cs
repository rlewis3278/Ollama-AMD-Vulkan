using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;

namespace OllamaToolkit.AiAssist;

public sealed class PlainLanguageErrorService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;

    public PlainLanguageErrorService(AiSettingsService? settings = null, OllamaApiClient? apiClient = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
    }

    public async Task<string> ExplainAsync(string rawError, CancellationToken cancellationToken = default)
    {
        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.PlainLanguageErrors, cancellationToken)
                .ConfigureAwait(false))
        {
            return Truncate(rawError, 240);
        }

        if (!await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return Truncate(rawError, 240);
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var model = settings.PreferredSummarizerModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return Truncate(rawError, 240);
        }

        try
        {
            var prompt = $"""
                Explain this Ollama/AMD Vulkan Windows error in one short actionable sentence for a laptop user.
                Error: {rawError}
                Explanation:
                """;
            var text = await _apiClient.GenerateAsync(model, prompt, 64, 2048, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? Truncate(rawError, 240) : text.Trim();
        }
        catch
        {
            return Truncate(rawError, 240);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";
}