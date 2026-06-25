using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.BenchmarkStore;

public static class LocalModelFilter
{
    public static bool IsBenchmarkableLocalModel(OllamaModelTag model)
    {
        if (model.Size <= 1_000_000)
        {
            return false;
        }

        if (model.Details is null)
        {
            return false;
        }

        return string.Equals(model.Details.Format, "gguf", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<OllamaModelTag> FilterBenchmarkable(
        IEnumerable<OllamaModelTag> models) =>
        models
            .Where(IsBenchmarkableLocalModel)
            .OrderBy(m => m.Size)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}