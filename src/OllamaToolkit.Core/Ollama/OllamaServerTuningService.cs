using OllamaToolkit.Core.Modes;

namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaServerTuningService
{
    public const string ParallelEnvVar = "OLLAMA_NUM_PARALLEL";

    public int? GetCurrentParallel()
    {
        var raw = System.Environment.GetEnvironmentVariable(ParallelEnvVar, EnvironmentVariableTarget.User);
        return int.TryParse(raw, out var value) ? BenchmarkParallelSettings.Clamp(value) : null;
    }

    public bool ApplyParallel(int parallel)
    {
        parallel = BenchmarkParallelSettings.Clamp(parallel);
        var current = GetCurrentParallel();
        if (current == parallel)
        {
            return false;
        }

        var text = parallel.ToString();
        System.Environment.SetEnvironmentVariable(ParallelEnvVar, text, EnvironmentVariableTarget.User);
        System.Environment.SetEnvironmentVariable(ParallelEnvVar, text, EnvironmentVariableTarget.Process);
        return true;
    }

    public bool ApplyParallelForMode(
        ComputeMode mode,
        IReadOnlyDictionary<string, int>? byMode,
        int fallback = 1) =>
        ApplyParallel(BenchmarkParallelSettings.GetParallel(mode, byMode, fallback));
}