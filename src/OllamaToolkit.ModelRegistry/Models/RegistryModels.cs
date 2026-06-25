namespace OllamaToolkit.ModelRegistry.Models;

public enum CatalogDescriptionDisplayMode
{
    Download,
    Ai
}

public sealed class CatalogRowViewModel
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string ListDescription { get; init; } = string.Empty;
    public string DownloadDescription { get; init; } = string.Empty;
    public string AiDescription { get; init; } = string.Empty;
    public string DisplayDescription { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string ParameterSize { get; init; } = "-";
    public string FileSize { get; init; } = "-";
    public string Tags { get; init; } = string.Empty;
    public bool Installed { get; init; }
    public int SortOrder { get; init; }
}

public sealed class TestResultRowViewModel
{
    public required string Model { get; init; }
    public string Category { get; init; } = string.Empty;
    public string BestMode { get; init; } = string.Empty;
    public double BestTps { get; init; }
    public string CpuResult { get; init; } = "-";
    public string ApuResult { get; init; } = "-";
    public string GpuResult { get; init; } = "-";
    public string HybridResult { get; init; } = "-";
    public string RocmResult { get; init; } = "-";
    public string Insight { get; init; } = string.Empty;
    public string LastTested { get; init; } = string.Empty;
    public string? ReportPath { get; init; }
}