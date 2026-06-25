namespace OllamaToolkit.ModelCatalog.Models;

public enum DescriptionRefreshPhase
{
    Started,
    Completed
}

public sealed class DescriptionRefreshItemProgress
{
    public required string ModelName { get; init; }
    public DescriptionRefreshPhase Phase { get; init; }
    public string? ListDescription { get; init; }
}