namespace OllamaToolkit.Core.Vulkan;

public sealed class VulkanDeviceMap
{
    public required string ApuVulkanIndex { get; init; }
    public required string GpuVulkanIndex { get; init; }
    public required string HybridVulkanValue { get; init; }
    public required string ApuName { get; init; }
    public required string GpuName { get; init; }
    public string TaskManagerNote { get; init; } =
        "Task Manager GPU 0 is often the RX 6700S and GPU 1 is the 680M on this laptop. Ollama uses Vulkan indices above, not Task Manager labels.";
}