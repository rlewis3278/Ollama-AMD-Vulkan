using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.ModelCatalog;

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
    public string SizeUsage { get; init; } = "-";
    public string FileSize { get; init; } = "-";
    public string ContextDisplay { get; init; } = "-";
    public string InputModalities { get; init; } = "-";
    public int ContextSortKey { get; init; }
    public long FileSizeSortKey { get; init; } = long.MaxValue;
    public long MetricSortKey { get; init; }
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
            SizeUsage = entry.SizeUsage,
            FileSize = entry.FileSize,
            ContextDisplay = entry.ContextDisplay,
            InputModalities = entry.InputModalities,
            ContextSortKey = entry.ContextSortKey > 0
                ? entry.ContextSortKey
                : CatalogMetadataLookup.ParseContextSortKey(entry.ContextDisplay),
            FileSizeSortKey = entry.FileSizeSortKey < long.MaxValue
                ? entry.FileSizeSortKey
                : CatalogFileSizeResolver.GetSortBytesFromDisplayLabel(
                    !string.IsNullOrWhiteSpace(entry.FileSize) && entry.FileSize != "-"
                        ? entry.FileSize
                        : entry.SizeUsage),
            MetricSortKey = entry.MetricSortKey,
            AiNote = entry.AiNote ?? string.Empty,
            CompositeScore = entry.CompositeScore,
            IsInstalled = installed
        };
}