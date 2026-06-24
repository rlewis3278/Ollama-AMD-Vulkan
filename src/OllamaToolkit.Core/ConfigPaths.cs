namespace OllamaToolkit.Core;

public static class ConfigPaths
{
    public static string ConfigDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".ollama-amd-vulkan");

    public static string EnvBackupFile => Path.Combine(ConfigDirectory, "env-backup.json");
    public static string AiSettingsFile => Path.Combine(ConfigDirectory, "ai-settings.json");
    public static string VulkanWorkaroundFile => Path.Combine(ConfigDirectory, "vulkan-workaround.json");
    public static string GuiAiActivityLog => Path.Combine(ConfigDirectory, "gui-ai-activity.log");

    public static string OllamaAppPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama app.exe");

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