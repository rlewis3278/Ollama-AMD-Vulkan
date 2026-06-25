namespace OllamaToolkit.ModelCatalog.Models;

public enum CatalogRefreshPhase
{
    Started,
    Completed
}

public sealed class CatalogRefreshItemProgress
{
    public required string ModelName { get; init; }
    public CatalogRefreshPhase Phase { get; init; }
    public bool IsInstalled { get; init; }
    public string? FileSize { get; init; }
    public string? Description { get; init; }
    public string? ParameterSize { get; init; }
}