namespace OllamaToolkit.BenchmarkRunner;

public enum GenerateModeMilestone
{
    ModeStarted,
    StoppingModel,
    ApplyingMode,
    ModeApplied,
    WarmingModel,
    ModelReady,
    WarmupStarted,
    WarmupComplete,
    Complete
}

public enum EmbedModeMilestone
{
    ModeStarted,
    ApplyingMode,
    ModeApplied,
    WarmingModel,
    ModelReady,
    EmbedWarmup,
    EmbedMeasure,
    Complete
}

public static class BenchmarkModeMilestones
{
    public static double GenerateFraction(GenerateModeMilestone milestone) => milestone switch
    {
        GenerateModeMilestone.ModeStarted => 0.00,
        GenerateModeMilestone.StoppingModel => 0.05,
        GenerateModeMilestone.ApplyingMode => 0.10,
        GenerateModeMilestone.ModeApplied => 0.20,
        GenerateModeMilestone.WarmingModel => 0.30,
        GenerateModeMilestone.ModelReady => 0.40,
        GenerateModeMilestone.WarmupStarted => 0.45,
        GenerateModeMilestone.WarmupComplete => 0.50,
        GenerateModeMilestone.Complete => 1.00,
        _ => 0
    };

    public static double EmbedFraction(EmbedModeMilestone milestone) => milestone switch
    {
        EmbedModeMilestone.ModeStarted => 0.00,
        EmbedModeMilestone.ApplyingMode => 0.10,
        EmbedModeMilestone.ModeApplied => 0.20,
        EmbedModeMilestone.WarmingModel => 0.30,
        EmbedModeMilestone.ModelReady => 0.40,
        EmbedModeMilestone.EmbedWarmup => 0.55,
        EmbedModeMilestone.EmbedMeasure => 0.75,
        EmbedModeMilestone.Complete => 1.00,
        _ => 0
    };

    public static double TokenFraction(int tokenCount, int numPredict)
    {
        var target = numPredict > 0 ? numPredict : 32;
        var clamped = Math.Clamp(tokenCount / (double)target, 0, 1);
        return 0.50 + 0.40 * clamped;
    }

    public static string GenerateStatus(GenerateModeMilestone milestone) => milestone switch
    {
        GenerateModeMilestone.ModeStarted => "Starting…",
        GenerateModeMilestone.StoppingModel => "Stopping prior model…",
        GenerateModeMilestone.ApplyingMode => "Applying mode & restarting Ollama…",
        GenerateModeMilestone.ModeApplied => "Mode applied — loading model…",
        GenerateModeMilestone.WarmingModel => "Warming model…",
        GenerateModeMilestone.ModelReady => "Model ready",
        GenerateModeMilestone.WarmupStarted => "Benchmark warmup…",
        GenerateModeMilestone.WarmupComplete => "Warmup complete",
        GenerateModeMilestone.Complete => "Complete",
        _ => "Benchmarking…"
    };

    public static string EmbedStatus(EmbedModeMilestone milestone) => milestone switch
    {
        EmbedModeMilestone.ModeStarted => "Starting…",
        EmbedModeMilestone.ApplyingMode => "Applying mode & restarting Ollama…",
        EmbedModeMilestone.ModeApplied => "Mode applied — loading model…",
        EmbedModeMilestone.WarmingModel => "Warming model…",
        EmbedModeMilestone.ModelReady => "Model ready",
        EmbedModeMilestone.EmbedWarmup => "Embed warmup…",
        EmbedModeMilestone.EmbedMeasure => "Running embed benchmark…",
        EmbedModeMilestone.Complete => "Complete",
        _ => "Embedding…"
    };

    public static string TokenStatus(int tokenCount, int numPredict)
    {
        var target = numPredict > 0 ? numPredict : 32;
        return $"Benchmarking… {Math.Min(tokenCount, target)}/{target} tokens";
    }
}