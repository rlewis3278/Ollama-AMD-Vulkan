namespace OllamaToolkit.ModelCategory.Models;

public sealed class UsageCategoryStoreDocument
{
    public int Version { get; set; } = 1;
    public string LastUpdated { get; set; } = DateTimeOffset.Now.ToString("o");
    public Dictionary<string, UsageCategoryEntry> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UsageCategoryEntry
{
    public string Category { get; set; } = "Other";
    public string? Subcategory { get; set; }
    public string? UsageSummary { get; set; }
    public string? LibraryName { get; set; }
    public string? FetchedAt { get; set; }
    public string? SummaryModel { get; set; }
    public bool AiClassified { get; set; }
    public string? Error { get; set; }
}