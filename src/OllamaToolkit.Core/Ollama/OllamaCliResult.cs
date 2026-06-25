namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaCliResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;

    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : $"{StandardOutput}\n{StandardError}".Trim();
}