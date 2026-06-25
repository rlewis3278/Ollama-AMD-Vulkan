using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.BenchmarkStore;

public static class LocalModelFilter
{
    public static bool IsBenchmarkableLocalModel(OllamaModelTag model)
    {
        if (model.Size <= 1_000_000 || string.IsNullOrWhiteSpace(model.Name))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(model.RemoteHost))
        {
            return false;
        }

        var format = model.Details?.Format;
        if (!string.IsNullOrWhiteSpace(format)
            && !string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static IReadOnlyList<OllamaModelTag> FilterBenchmarkable(
        IEnumerable<OllamaModelTag> models) =>
        models
            .Where(IsBenchmarkableLocalModel)
            .OrderBy(m => m.Size)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}