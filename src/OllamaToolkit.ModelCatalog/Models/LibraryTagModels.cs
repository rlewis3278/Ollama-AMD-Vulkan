namespace OllamaToolkit.ModelCatalog.Models;

public sealed class LibraryTagInfo
{
    public required string PullTag { get; init; }
    public string? FileSizeLabel { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? SizeUsage { get; init; }
    public string? ContextWindow { get; init; }
    public string? InputModalities { get; init; }
    public bool IsCloud { get; init; }
}

public sealed class CatalogPullResolution
{
    public required string PullTag { get; init; }
    public bool Resolved { get; init; }
    public bool IsCloudOnly { get; init; }
    public string ParameterSize { get; init; } = "-";
    public string FileSize { get; init; } = "-";
}