namespace OllamaToolkit.ModelCatalog.Models;

public sealed record CatalogMergeResult(
    IReadOnlyList<string> AddedNames,
    int TotalCatalogCount);