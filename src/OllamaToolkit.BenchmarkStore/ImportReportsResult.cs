namespace OllamaToolkit.BenchmarkStore;

public sealed class ImportReportsResult
{
    public IReadOnlyList<string> ModelNames { get; init; } = [];
    public int Count => ModelNames.Count;
}