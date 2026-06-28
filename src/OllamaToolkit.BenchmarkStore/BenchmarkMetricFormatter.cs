using OllamaToolkit.BenchmarkStore.Models;

namespace OllamaToolkit.BenchmarkStore;

public static class BenchmarkMetricFormatter
{
    public static string Format(string? benchmarkKind, double bestTps, double bestEmbedMs)
    {
        var kind = string.IsNullOrWhiteSpace(benchmarkKind) ? BenchmarkKinds.Generate : benchmarkKind;
        return kind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase)
            ? bestEmbedMs > 0 ? $"{bestEmbedMs:F1} ms" : "-"
            : bestTps > 0 ? $"{bestTps:F1}" : "-";
    }
}