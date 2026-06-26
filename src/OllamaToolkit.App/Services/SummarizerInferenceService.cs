using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.Core.Modes;

namespace OllamaToolkit.App.Services;

public sealed class SummarizerInferenceService
{
    private readonly ProfileStoreService _profiles;
    private readonly ModeService _modeService;
    private readonly ToolkitDiagnosticsService _diagnostics;

    public SummarizerInferenceService(
        ProfileStoreService profiles,
        ModeService modeService,
        ToolkitDiagnosticsService diagnostics)
    {
        _profiles = profiles;
        _modeService = modeService;
        _diagnostics = diagnostics;
    }

    public async Task ApplySummarizerBestModeAsync(
        string summarizerModel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(summarizerModel))
        {
            return;
        }

        var summaries = await _profiles.GetAllSummariesAsync(cancellationToken).ConfigureAwait(false);
        var match = summaries.FirstOrDefault(s =>
            s.Model.Equals(summarizerModel, StringComparison.OrdinalIgnoreCase)
            || s.Model.StartsWith($"{summarizerModel.Split(':')[0]}:", StringComparison.OrdinalIgnoreCase));

        if (match is null || match.NeedsRetest || string.IsNullOrWhiteSpace(match.BestMode))
        {
            _diagnostics.Write("AI",
                $"Summarizer {summarizerModel}: no tested profile — keeping current compute mode.");
            return;
        }

        if (!Enum.TryParse<ComputeMode>(match.BestMode, out var mode))
        {
            _diagnostics.Write("AI",
                $"Summarizer {summarizerModel}: unrecognized best mode '{match.BestMode}'.");
            return;
        }

        await _modeService.ApplyModeWithRestartAsync(mode, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _diagnostics.Write("AI",
            $"Summarizer inference: applied best mode {match.BestMode} for {summarizerModel}.");
    }
}