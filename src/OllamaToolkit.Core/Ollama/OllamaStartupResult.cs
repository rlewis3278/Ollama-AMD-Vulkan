namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaStartupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool StartedTrayApp { get; init; }
    public bool StartedServeProcess { get; init; }
}