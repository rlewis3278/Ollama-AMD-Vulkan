using OllamaToolkit.AiAssist.Models;

namespace OllamaToolkit.App.Services;

public sealed class AiRecommendedLlmRowViewModel
{
    public int DisplayRank { get; init; }
    public string Model { get; init; } = string.Empty;
    public string PullTag { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string BestMode { get; init; } = "-";
    public string MetricDisplay { get; init; } = "-";
    public string ParameterSize { get; init; } = "-";
    public string FileSize { get; init; } = "-";
    public string AiNote { get; init; } = string.Empty;
    public double CompositeScore { get; init; }
    public bool IsInstalled { get; init; }

    public static AiRecommendedLlmRowViewModel FromEntry(AiRecommendedLlmEntry entry, bool installed) =>
        new()
        {
            DisplayRank = entry.DisplayRank,
            Model = entry.ModelOrLibrary,
            PullTag = string.IsNullOrWhiteSpace(entry.PullTag)
                ? $"{entry.ModelOrLibrary}:latest"
                : entry.PullTag,
            Category = entry.Category,
            BestMode = string.IsNullOrWhiteSpace(entry.BestMode) ? "-" : entry.BestMode,
            MetricDisplay = string.IsNullOrWhiteSpace(entry.MetricDisplay) ? "-" : entry.MetricDisplay,
            ParameterSize = entry.ParameterSize,
            FileSize = entry.FileSize,
            AiNote = entry.AiNote ?? string.Empty,
            CompositeScore = entry.CompositeScore,
            IsInstalled = installed
        };
}