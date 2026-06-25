using OllamaToolkit.Core.Modes;

namespace OllamaToolkit.BenchmarkRunner;

public static class BenchmarkProgressFormatter
{
    public static string ModelHeader(
        string model,
        int modelIndex,
        int modelCount,
        int numCtx,
        int numPredict,
        IReadOnlyList<ComputeMode> modes,
        string? aiSummarizerModel)
    {
        var lines = new List<string>
        {
            $"Benchmark model: {model} ({modelIndex + 1}/{modelCount} in queue)",
            $"Kind: generation (/api/generate)",
            $"Settings: num_ctx={numCtx}, num_predict={numPredict}",
            $"Modes: {string.Join(", ", modes)}"
        };

        if (!string.IsNullOrWhiteSpace(aiSummarizerModel))
        {
            lines.Add($"AI insights LLM: {aiSummarizerModel}");
        }
        else
        {
            lines.Add("AI insights LLM: (none — post-test AI analysis disabled)");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string EmbedModelHeader(
        string model,
        int modelIndex,
        int modelCount,
        IReadOnlyList<ComputeMode> modes,
        string? aiSummarizerModel)
    {
        var lines = new List<string>
        {
            $"Benchmark model: {model} ({modelIndex + 1}/{modelCount} in queue)",
            "Kind: embedding (/api/embed)",
            "Metric: embed latency (ms) — lower is faster",
            $"Modes: {string.Join(", ", modes)}"
        };

        if (!string.IsNullOrWhiteSpace(aiSummarizerModel))
        {
            lines.Add($"AI insights LLM: {aiSummarizerModel}");
        }
        else
        {
            lines.Add("AI insights LLM: (none — post-test AI analysis disabled)");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string ModeApplying(int modeIndex, int modeCount, ComputeMode mode) =>
        $"[{modeIndex + 1}/{modeCount}] {mode} — Applying compute mode and restarting Ollama...";

    public static string ModeAppliedWithRestart(ComputeMode mode, string envSummary) =>
        $"[{mode}] Ollama restarted with backend env: {envSummary}";

    public static string ModeBenchmarking(int modeIndex, int modeCount, ComputeMode mode) =>
        $"[{modeIndex + 1}/{modeCount}] {mode} — Benchmarking generation...";

    public static string ModeEmbedBenchmarking(int modeIndex, int modeCount, ComputeMode mode) =>
        $"[{modeIndex + 1}/{modeCount}] {mode} — Benchmarking embedding...";

    public static string ModeCompleted(
        int modeIndex,
        int modeCount,
        ComputeMode mode,
        double generationTps,
        double durationSec) =>
        $"[{modeIndex + 1}/{modeCount}] {mode} — OK: {generationTps:F2} tok/s ({durationSec:F1}s)";

    public static string ModeEmbedCompleted(
        int modeIndex,
        int modeCount,
        ComputeMode mode,
        double latencyMs,
        double durationSec) =>
        $"[{modeIndex + 1}/{modeCount}] {mode} — OK: {latencyMs:F1} ms/embed ({durationSec:F1}s)";

    public static string ModeFailed(int modeIndex, int modeCount, ComputeMode mode, string error) =>
        $"[{modeIndex + 1}/{modeCount}] {mode} — FAIL: {error}";

    public static string ModelWinner(string mode, double generationTps) =>
        $"Winner: {mode} @ {generationTps:F2} tok/s";

    public static string EmbedModelWinner(string mode, double latencyMs) =>
        $"Winner: {mode} @ {latencyMs:F1} ms/embed";

    public static string ReportSaved(string path) => $"Report saved: {path}";
}