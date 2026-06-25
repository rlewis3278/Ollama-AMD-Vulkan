namespace OllamaToolkit.Core.Modes;

public sealed class ModeDefinition
{
    public required ComputeMode Mode { get; init; }
    public required string Label { get; init; }
    public required string ShortLabel { get; init; }
    public required string Description { get; init; }
    public required string CardSubtitle { get; init; }
    public bool Requires680MWorkaround { get; init; }
    public required IReadOnlyDictionary<string, string> Variables { get; init; }
}