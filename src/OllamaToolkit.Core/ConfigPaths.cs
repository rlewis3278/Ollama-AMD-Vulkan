namespace OllamaToolkit.Core;

public static class ConfigPaths
{
    public static string ConfigDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".ollama-amd-vulkan");

    public static string EnvBackupFile => Path.Combine(ConfigDirectory, "env-backup.json");
    public static string AiSettingsFile => Path.Combine(ConfigDirectory, "ai-settings.json");
    public static string LibraryCatalogStoreFile => Path.Combine(ConfigDirectory, "library-catalog-store.json");
    public static string ModelDescriptionsFile => Path.Combine(ConfigDirectory, "model-descriptions.json");
    public static string ModelUsageCategoriesFile => Path.Combine(ConfigDirectory, "model-usage-categories.json");
    public static string ModelBenchmarkInsightsFile => Path.Combine(ConfigDirectory, "model-benchmark-insights.json");
    public static string ModelBenchmarkSettingsFile => Path.Combine(ConfigDirectory, "model-benchmark-settings.json");
    public static string ModelComparisonCacheFile => Path.Combine(ConfigDirectory, "model-comparison-cache.json");
    public static string NlSearchCacheFile => Path.Combine(ConfigDirectory, "nl-search-cache.json");
    public static string LogAnomaliesFile => Path.Combine(ConfigDirectory, "log-anomalies.json");
    public static string VulkanWorkaroundFile => Path.Combine(ConfigDirectory, "vulkan-workaround.json");
    public static string GuiAiActivityLog => Path.Combine(ConfigDirectory, "gui-ai-activity.log");
    public static string ToolkitDiagnosticsLog => Path.Combine(ConfigDirectory, "toolkit-diagnostics.log");

    public static string OllamaAppPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama app.exe");

    public static string OllamaServeExePath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");

    public static IEnumerable<string> OllamaAppCandidates()
    {
        yield return OllamaAppPath;
        yield return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            "Ollama", "ollama app.exe");
    }

    public static IEnumerable<string> OllamaServeCandidates()
    {
        yield return OllamaServeExePath;
        yield return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            "Ollama", "ollama.exe");
    }

    public static string DefaultOllamaHost => "http://localhost:11434";

    public static readonly string[] ManagedEnvironmentVariables =
    [
        "OLLAMA_VULKAN",
        "HIP_VISIBLE_DEVICES",
        "GGML_VK_VISIBLE_DEVICES",
        "ROCR_VISIBLE_DEVICES",
        "CUDA_VISIBLE_DEVICES",
        "OLLAMA_NUM_GPU",
        "OLLAMA_IGPU_ENABLE"
    ];

    public static void EnsureConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
    }
}