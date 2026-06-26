namespace OllamaToolkit.BenchmarkRunner;

/// <summary>
/// Continuously creeps ModeFraction toward a rising ceiling with seamless retargeting.
/// </summary>
public sealed class BenchmarkProgressAnimator : IDisposable
{
    private readonly IProgress<BenchmarkProgressUpdate>? _progress;
    private readonly Func<BenchmarkProgressUpdate> _templateFactory;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _creepTask;
    private double _ceiling;
    private double _idleRatePerSecond;
    private string _detail = string.Empty;
    private int _intervalMs = 50;

    public BenchmarkProgressAnimator(
        IProgress<BenchmarkProgressUpdate>? progress,
        Func<BenchmarkProgressUpdate> templateFactory)
    {
        _progress = progress;
        _templateFactory = templateFactory;
    }

    public double CurrentFraction { get; private set; }

    public void Report(double fraction, string? detail = null)
    {
        CurrentFraction = Math.Clamp(fraction, 0, 1);
        lock (_sync)
        {
            _ceiling = Math.Max(_ceiling, CurrentFraction);
            if (detail is not null)
            {
                _detail = detail;
            }
        }

        if (_progress is null)
        {
            return;
        }

        var template = _templateFactory();
        _progress.Report(Copy(template, CurrentFraction, detail ?? _detail));
    }

    public void RunToward(double ceiling, string detail, int intervalMs = 50)
    {
        ceiling = Math.Clamp(ceiling, 0, 1);
        lock (_sync)
        {
            _ceiling = Math.Max(_ceiling, Math.Max(CurrentFraction, ceiling));
            _detail = detail;
            _intervalMs = intervalMs;
            _idleRatePerSecond = 0;
        }

        EnsureCreepLoop();
    }

    public void SetIdleAdvance(double ratePerSecond, string detail, int intervalMs = 50)
    {
        lock (_sync)
        {
            _idleRatePerSecond = Math.Max(0, ratePerSecond);
            _detail = detail;
            _intervalMs = intervalMs;
        }

        EnsureCreepLoop();
    }

    /// <summary>Legacy API — delegates to <see cref="RunToward"/>.</summary>
    public void StartCreep(double from, double to, string detail, int intervalMs = 50) =>
        RunToward(Math.Max(from, to), detail, intervalMs);

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

        lock (_sync)
        {
            _idleRatePerSecond = 0;
        }
    }

    public void Dispose() => StopCreep();

    private void EnsureCreepLoop()
    {
        if (_creepTask is { IsCompleted: false })
        {
            return;
        }

        StopCreep();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _creepTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                double ceiling;
                double idleRate;
                string detail;
                int interval;
                double fraction;
                lock (_sync)
                {
                    ceiling = _ceiling;
                    idleRate = _idleRatePerSecond;
                    detail = _detail;
                    interval = _intervalMs;
                    fraction = CurrentFraction;
                }

                if (fraction < ceiling - 0.0001)
                {
                    var range = ceiling - fraction;
                    var steps = Math.Max(100, (int)Math.Ceiling(range * 300));
                    var step = range / steps;
                    Report(Math.Min(ceiling, fraction + step), detail);
                }
                else if (idleRate > 0 && fraction < 0.98)
                {
                    var next = Math.Min(0.98, fraction + idleRate * interval / 1000.0);
                    lock (_sync)
                    {
                        _ceiling = Math.Max(_ceiling, next);
                    }

                    Report(next, detail);
                }

                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }, ct);
    }

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