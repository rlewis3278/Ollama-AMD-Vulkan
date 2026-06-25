namespace OllamaToolkit.Core.Ollama;

public static class ModelInstallMatcher
{
    public static string LibraryName(string modelOrTag) =>
        modelOrTag.Split(':')[0];

    public static bool IsLibraryInstalled(string libraryName, IReadOnlySet<string> installedTagNames) =>
        installedTagNames.Contains(libraryName)
        || installedTagNames.Any(n => n.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase));
}