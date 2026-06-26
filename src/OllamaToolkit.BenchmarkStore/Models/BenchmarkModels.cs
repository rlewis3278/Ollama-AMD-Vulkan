using System.Text.Json.Serialization;

namespace OllamaToolkit.BenchmarkStore.Models;

public sealed class ModelProfileStoreDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, ModelProfileEntry> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class BenchmarkKinds
{
    public const string Generate = "Generate";
    public const string Embed = "Embed";
}

public sealed class ModelProfileEntry
{
    public string BenchmarkKind { get; set; } = BenchmarkKinds.Generate;
    public string? BestMode { get; set; }
    public double BestTps { get; set; }

    [JsonPropertyName("BestEmbed_ms")]
    public double BestEmbedMs { get; set; }
    public string? Quantization { get; set; }
    public int NumCtx { get; set; }
    public int NumPredict { get; set; }
    public Dictionary<string, int>? NumParallelByMode { get; set; }
    public int Runs { get; set; }
    public string? LastTested { get; set; }
    public Dictionary<string, ModeResultEntry>? Results { get; set; }
    public string? ReportPath { get; set; }
    public string? OutputDir { get; set; }
    public string? Digest { get; set; }
}

public sealed class ModeResultEntry
{
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("Generation_tps")]
    public double GenerationTps { get; set; }

    [JsonPropertyName("PromptEval_tps")]
    public double PromptEvalTps { get; set; }

    [JsonPropertyName("TTFT_ms")]
    public double TtftMs { get; set; }

    [JsonPropertyName("EmbedLatency_ms")]
    public double EmbedLatencyMs { get; set; }

    [JsonPropertyName("VRAM_MB")]
    public double VramMb { get; set; }

    public string? Notes { get; set; }
    public string? Error { get; set; }
}

public sealed class BenchmarkReportDocument
{
    public string BenchmarkKind { get; set; } = BenchmarkKinds.Generate;
    public string? EmbedInput { get; set; }
    public string? StartedAt { get; set; }
    public string? Model { get; set; }
    public string? Quantization { get; set; }
    public int NumPredict { get; set; }
    public int NumCtx { get; set; }
    public Dictionary<string, int>? NumParallelByMode { get; set; }
    public int Runs { get; set; }
    public string? CompletedAt { get; set; }
    public string? OutputDir { get; set; }
    public List<BenchmarkReportModeResult>? Results { get; set; }
    public BenchmarkReportWinner? Winner { get; set; }
}

public sealed class BenchmarkReportModeResult
{
    public string Mode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("Generation_tps")]
    public double GenerationTps { get; set; }

    [JsonPropertyName("PromptEval_tps")]
    public double PromptEvalTps { get; set; }

    [JsonPropertyName("TTFT_ms")]
    public double TtftMs { get; set; }

    [JsonPropertyName("EmbedLatency_ms")]
    public double EmbedLatencyMs { get; set; }

    [JsonPropertyName("VRAM_MB")]
    public double VramMb { get; set; }

    public string? Notes { get; set; }
    public string? Error { get; set; }
    public double DurationSec { get; set; }
}

public sealed class BenchmarkReportWinner
{
    public string? Mode { get; set; }

    [JsonPropertyName("Generation_tps")]
    public double GenerationTps { get; set; }

    [JsonPropertyName("TTFT_ms")]
    public double TtftMs { get; set; }

    [JsonPropertyName("EmbedLatency_ms")]
    public double EmbedLatencyMs { get; set; }

    [JsonPropertyName("VRAM_MB")]
    public double VramMb { get; set; }
}

public sealed class ModelProfileSummary
{
    public required string Model { get; init; }
    public string BenchmarkKind { get; init; } = BenchmarkKinds.Generate;
    public double SizeGB { get; init; }
    public string FileSize => OllamaToolkit.Core.ModelSizeFormatter.FormatGb(SizeGB);
    public string Quantization { get; init; } = "-";
    public string ParameterSize { get; init; } = "-";
    public string Digest { get; init; } = string.Empty;
    public string Status { get; init; } = "Untested";
    public string BestMode { get; init; } = string.Empty;
    public double BestTps { get; init; }
    public double BestEmbedMs { get; init; }
    public string BestMetricDisplay =>
        BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase)
            ? BestEmbedMs > 0 ? $"{BestEmbedMs:F1} ms" : "-"
            : BestTps > 0 ? $"{BestTps:F1}" : "-";
    public string LastTested { get; init; } = string.Empty;
    public bool NeedsRetest { get; init; } = true;
    public int RecommendedCtx { get; init; }
    public string Category { get; init; } = string.Empty;
    public Dictionary<string, ModeResultEntry>? Results { get; init; }
}