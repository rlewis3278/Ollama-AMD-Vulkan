namespace OllamaToolkit.ModelCatalog.Models;

public sealed class LibraryCatalogStoreDocument
{
    public int Version { get; set; } = 2;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public string? CatalogFetchedAt { get; set; }
    public string? SortGeneratedAt { get; set; }
    public string? SummaryModel { get; set; }
    public List<LibraryCatalogEntry> Items { get; set; } = new();
}

public sealed class LibraryCatalogEntry
{
    public string Name { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string ParameterSize { get; set; } = "-";
    public string SizeUsage { get; set; } = "-";
    public string ContextWindow { get; set; } = "-";
    public string InputModalities { get; set; } = "-";
    public string FileSize { get; set; } = "-";
    public string EstimatedFileSize { get; set; } = "-";
    public long FileSizeBytes { get; set; }
    public bool FileSizeConfirmed { get; set; }
    public string? FileSizeConfirmedAt { get; set; }
    public string DefaultPullTag { get; set; } = string.Empty;
    public bool IsCloudOnly { get; set; }
    public string Category { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string ListDescription { get; set; } = string.Empty;
}

public sealed class ModelDescriptionStoreDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, ModelDescriptionEntry> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelDescriptionEntry
{
    public string? ShortDescription { get; set; }
    public string? ListDescription { get; set; }
    public string? Title { get; set; }
    public string? SourceUrl { get; set; }
    public string? LibraryName { get; set; }
    public string? FetchedAt { get; set; }
    public string? SummaryModel { get; set; }
    public string? SummaryGeneratedAt { get; set; }
    public string? Error { get; set; }
}