namespace OllamaToolkit.BenchmarkRunner;

public enum BenchmarkProgressPhase
{
    QueueStarted,
    ModelStarted,
    ModeApplying,
    ModeBenchmarking,
    ModeCompleted,
    ModeFailed,
    ModelCompleted,
    QueueCompleted
}

public sealed class BenchmarkProgressUpdate
{
    public BenchmarkProgressPhase Phase { get; init; }
    public string? Model { get; init; }
    public int ModelIndex { get; init; }
    public int ModelCount { get; init; }
    public string? Mode { get; init; }
    public int ModeIndex { get; init; }
    public int ModeCount { get; init; }
    public int NumCtx { get; init; }
    public int NumPredict { get; init; }
    public double? GenerationTps { get; init; }
    public double DurationSec { get; init; }
    public string? Error { get; init; }
    public string? BestMode { get; init; }
    public double BestTps { get; init; }
    public string? LogLine { get; init; }
    public string? AiSummarizerModel { get; init; }

    public double CurrentModePercent => Phase switch
    {
        BenchmarkProgressPhase.ModeApplying => 25,
        BenchmarkProgressPhase.ModeBenchmarking => 65,
        BenchmarkProgressPhase.ModeCompleted or BenchmarkProgressPhase.ModeFailed => 100,
        _ => 0
    };

    public double OverallPercent
    {
        get
        {
            if (ModelCount <= 0 || ModeCount <= 0)
            {
                return 0;
            }

            var modeWeight = Phase switch
            {
                BenchmarkProgressPhase.ModeApplying => 0.2,
                BenchmarkProgressPhase.ModeBenchmarking => 0.6,
                BenchmarkProgressPhase.ModeCompleted or BenchmarkProgressPhase.ModeFailed => 1.0,
                _ => 0.0
            };

            var completed = ModelIndex * ModeCount + ModeIndex + modeWeight;
            return Math.Clamp(completed / (ModelCount * ModeCount) * 100.0, 0, 100);
        }
    }

    public double ModePercent
    {
        get
        {
            if (ModeCount <= 0)
            {
                return 0;
            }

            var modeWeight = Phase is BenchmarkProgressPhase.ModeCompleted or BenchmarkProgressPhase.ModeFailed
                ? 1.0
                : Phase == BenchmarkProgressPhase.ModeBenchmarking ? 0.65 : 0.25;
            return Math.Clamp((ModeIndex + modeWeight) / ModeCount * 100.0, 0, 100);
        }
    }
}