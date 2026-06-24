namespace OllamaToolkit.AiAssist.Models;

public sealed class BenchmarkInsightsDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, BenchmarkInsightEntry> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BenchmarkInsightEntry
{
    public string? Interpretation { get; set; }
    public Dictionary<string, string>? FailureDiagnosis { get; set; }
    public string? SummaryModel { get; set; }
    public string? GeneratedAt { get; set; }
}

public sealed class LogAnomaliesDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public List<LogAnomalyEntry> Anomalies { get; set; } = new();
}

public sealed class LogAnomalyEntry
{
    public string Pattern { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string DetectedAt { get; set; } = DateTimeOffset.Now.ToString("o");
}