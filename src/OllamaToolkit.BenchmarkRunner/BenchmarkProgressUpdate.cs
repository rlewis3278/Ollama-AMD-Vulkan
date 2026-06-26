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

public sealed record BenchmarkProgressUpdate
{
    public string BenchmarkKind { get; init; } = "Generate";
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
    public double? EmbedLatencyMs { get; init; }
    public double DurationSec { get; init; }
    public string? Error { get; init; }
    public string? BestMode { get; init; }
    public double BestTps { get; init; }
    public double BestEmbedMs { get; init; }
    public string? LogLine { get; init; }
    public string? AiSummarizerModel { get; init; }

    /// <summary>0.0–1.0 progress within the current mode. Negative = derive from <see cref="Phase"/>.</summary>
    public double ModeFraction { get; init; } = -1;

    public string? ModeStatusDetail { get; init; }

    /// <summary>Milestone name for UI labels (e.g. ApplyingMode, Generating).</summary>
    public string? ModeMilestone { get; init; }

    public double EffectiveModeFraction =>
        ModeFraction >= 0
            ? Math.Clamp(ModeFraction, 0, 1)
            : Phase switch
            {
                BenchmarkProgressPhase.ModeCompleted or BenchmarkProgressPhase.ModeFailed => 1.0,
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

            var completed = ModelIndex * ModeCount + ModeIndex + EffectiveModeFraction;
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

            return Math.Clamp((ModeIndex + EffectiveModeFraction) / ModeCount * 100.0, 0, 100);
        }
    }

    public double CurrentModePercent => EffectiveModeFraction * 100.0;

    public bool UseIndeterminate =>
        Phase is BenchmarkProgressPhase.ModeApplying or BenchmarkProgressPhase.ModeBenchmarking
        && ModeFraction < 0
        && string.IsNullOrWhiteSpace(ModeStatusDetail);
}