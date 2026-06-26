namespace OllamaToolkit.AiAssist.Models;

public sealed class NlSearchCacheDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, NlSearchCacheEntry> Queries { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NlSearchCacheEntry
{
    public string Query { get; set; } = string.Empty;
    public List<string> RankedModels { get; set; } = new();
    public string? SummaryModel { get; set; }
    public string GeneratedAt { get; set; } = DateTimeOffset.Now.ToString("o");
}

public sealed class BenchmarkSettingsDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, BenchmarkSettingsEntry> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BenchmarkSettingsEntry
{
    public int NumCtx { get; set; } = 8192;
    public int NumPredict { get; set; } = 32;
    public Dictionary<string, int>? NumParallelByMode { get; set; }
    public string? Rationale { get; set; }
    public string? SummaryModel { get; set; }
    public string GeneratedAt { get; set; } = DateTimeOffset.Now.ToString("o");
}

public sealed class ModelComparisonCacheDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, ModelComparisonCacheEntry> Pairs { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelComparisonCacheEntry
{
    public string ModelA { get; set; } = string.Empty;
    public string ModelB { get; set; } = string.Empty;
    public string Comparison { get; set; } = string.Empty;
    public string? ProfileDigest { get; set; }
    public string? SummaryModel { get; set; }
    public string GeneratedAt { get; set; } = DateTimeOffset.Now.ToString("o");
}

public sealed class ModelAdvisorRecommendation
{
    public required string Model { get; init; }
    public string BestMode { get; init; } = string.Empty;
    public double BestTps { get; init; }
    public string Category { get; init; } = string.Empty;
    public int Rank { get; init; }
}

public sealed class AiLlmRecommendationsDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public string? Intent { get; set; }
    public string GeneratedAt { get; set; } = DateTimeOffset.Now.ToString("o");
    public string? SummaryModel { get; set; }
    public List<AiRecommendedLlmEntry> Installed { get; set; } = new();
    public List<AiRecommendedLlmEntry> Uninstalled { get; set; } = new();
}

public sealed class AiRecommendedLlmEntry
{
    public string ModelOrLibrary { get; set; } = string.Empty;
    public string PullTag { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ParameterSize { get; set; } = "-";
    public string FileSize { get; set; } = "-";
    public int AiRank { get; set; }
    public double BenchmarkScore { get; set; }
    public double CompositeScore { get; set; }
    public string BestMode { get; set; } = string.Empty;
    public string MetricDisplay { get; set; } = "-";
    public string? AiNote { get; set; }
    public int DisplayRank { get; set; }
}