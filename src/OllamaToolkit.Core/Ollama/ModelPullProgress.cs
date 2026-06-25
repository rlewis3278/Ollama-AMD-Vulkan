namespace OllamaToolkit.Core.Ollama;

public sealed class ModelPullProgress
{
    public string Status { get; init; } = string.Empty;
    public int? Percent { get; init; }
}