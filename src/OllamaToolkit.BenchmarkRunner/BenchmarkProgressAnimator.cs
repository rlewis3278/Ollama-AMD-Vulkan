namespace OllamaToolkit.BenchmarkRunner;

/// <summary>
/// Creeps ModeFraction while long-running benchmark steps execute (mode apply, embed).
/// </summary>
public sealed class BenchmarkProgressAnimator : IDisposable
{
    private readonly IProgress<BenchmarkProgressUpdate>? _progress;
    private readonly Func<BenchmarkProgressUpdate> _templateFactory;
    private CancellationTokenSource? _cts;
    private Task? _creepTask;

    public BenchmarkProgressAnimator(
        IProgress<BenchmarkProgressUpdate>? progress,
        Func<BenchmarkProgressUpdate> templateFactory)
    {
        _progress = progress;
        _templateFactory = templateFactory;
    }

    public void Report(double fraction, string? detail = null)
    {
        if (_progress is null)
        {
            return;
        }

        var template = _templateFactory();
        _progress.Report(Copy(template, Math.Clamp(fraction, 0, 1), detail));
    }

    public void StartCreep(double from, double to, string detail, int intervalMs = 250)
    {
        StopCreep();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _creepTask = Task.Run(async () =>
        {
            var fraction = from;
            var step = (to - from) / Math.Max(1, (int)((to - from) * 40));
            while (!ct.IsCancellationRequested && fraction < to)
            {
                Report(fraction, detail);
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                fraction = Math.Min(to, fraction + step);
            }
        }, ct);
    }

    public void StopCreep()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _creepTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Creep task may throw on cancel.
        }

        _cts.Dispose();
        _cts = null;
        _creepTask = null;
    }

    public void Dispose() => StopCreep();

    private static BenchmarkProgressUpdate Copy(
        BenchmarkProgressUpdate source,
        double modeFraction,
        string? detail) =>
        new()
        {
            BenchmarkKind = source.BenchmarkKind,
            Phase = source.Phase,
            Model = source.Model,
            ModelIndex = source.ModelIndex,
            ModelCount = source.ModelCount,
            Mode = source.Mode,
            ModeIndex = source.ModeIndex,
            ModeCount = source.ModeCount,
            NumCtx = source.NumCtx,
            NumPredict = source.NumPredict,
            GenerationTps = source.GenerationTps,
            EmbedLatencyMs = source.EmbedLatencyMs,
            DurationSec = source.DurationSec,
            Error = source.Error,
            BestMode = source.BestMode,
            BestTps = source.BestTps,
            BestEmbedMs = source.BestEmbedMs,
            LogLine = source.LogLine,
            AiSummarizerModel = source.AiSummarizerModel,
            ModeFraction = modeFraction,
            ModeStatusDetail = detail
        };
}