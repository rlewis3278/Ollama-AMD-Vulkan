namespace OllamaToolkit.BenchmarkRunner;

public static class BenchmarkProgress
{
    public static void Report(
        IProgress<BenchmarkProgressUpdate>? progress,
        BenchmarkProgressUpdate update) =>
        progress?.Report(update);

    public static void Log(
        IProgress<string>? log,
        BenchmarkProgressUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.LogLine))
        {
            log?.Report(update.LogLine);
        }
    }

    public static void ReportAndLog(
        IProgress<BenchmarkProgressUpdate>? progress,
        IProgress<string>? log,
        BenchmarkProgressUpdate update)
    {
        Report(progress, update);
        Log(log, update);
    }
}