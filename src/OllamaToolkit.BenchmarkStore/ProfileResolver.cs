using OllamaToolkit.BenchmarkStore.Models;

namespace OllamaToolkit.BenchmarkStore;

public static class ProfileResolver
{
    public static ModelProfileEntry? ResolveForLibrary(string libraryName, ModelProfileStoreDocument profileDoc)
    {
        if (profileDoc.Models.TryGetValue(libraryName, out var direct))
        {
            return direct;
        }

        foreach (var (key, entry) in profileDoc.Models)
        {
            if (key.Equals(libraryName, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}