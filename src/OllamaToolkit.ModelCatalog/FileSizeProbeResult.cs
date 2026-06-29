namespace OllamaToolkit.ModelCatalog;

public sealed class FileSizeProbeResult
{
    public int Probed { get; init; }
    public int Failed { get; init; }
    public int SkippedCloud { get; init; }
    public int SkippedUnresolved { get; init; }
}