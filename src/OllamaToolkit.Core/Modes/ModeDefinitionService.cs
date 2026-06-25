using OllamaToolkit.Core.Vulkan;

namespace OllamaToolkit.Core.Modes;

public sealed class ModeDefinitionService
{
    private readonly VulkanDeviceMap _deviceMap;
    private readonly IReadOnlyDictionary<ComputeMode, ModeDefinition> _definitions;

    public ModeDefinitionService(VulkanDeviceDiscovery? discovery = null)
    {
        _deviceMap = TryDiscoverDeviceMap(discovery);
        _definitions = BuildDefinitions(_deviceMap);
    }

    public bool UsedFallbackDeviceMap { get; private set; }

    private VulkanDeviceMap TryDiscoverDeviceMap(VulkanDeviceDiscovery? discovery)
    {
        try
        {
            UsedFallbackDeviceMap = false;
            return (discovery ?? new VulkanDeviceDiscovery()).Discover();
        }
        catch
        {
            UsedFallbackDeviceMap = true;
            return new VulkanDeviceMap
            {
                ApuVulkanIndex = "0",
                GpuVulkanIndex = "1",
                HybridVulkanValue = "0,1",
                ApuName = "Radeon 680M (fallback)",
                GpuName = "RX 6700S (fallback)"
            };
        }
    }

    public VulkanDeviceMap DeviceMap => _deviceMap;

    public IReadOnlyDictionary<ComputeMode, ModeDefinition> Definitions => _definitions;

    public ModeDefinition Get(ComputeMode mode) => _definitions[mode];

    public bool TryParse(string? text, out ComputeMode mode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            mode = default;
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out mode);
    }

    private static IReadOnlyDictionary<ComputeMode, ModeDefinition> BuildDefinitions(VulkanDeviceMap map)
    {
        return new Dictionary<ComputeMode, ModeDefinition>
        {
            [ComputeMode.CPU] = new ModeDefinition
            {
                Mode = ComputeMode.CPU,
                Label = "CPU Only",
                ShortLabel = "CPU Only",
                CardSubtitle = "CPU inference only",
                Description = "Disable Vulkan and HIP; force CPU inference.",
                Variables = new Dictionary<string, string>
                {
                    ["OLLAMA_VULKAN"] = "0",
                    ["HIP_VISIBLE_DEVICES"] = "-1",
                    ["GGML_VK_VISIBLE_DEVICES"] = "-1",
                    ["ROCR_VISIBLE_DEVICES"] = "-1",
                    ["OLLAMA_IGPU_ENABLE"] = "0"
                }
            },
            [ComputeMode.APU] = new ModeDefinition
            {
                Mode = ComputeMode.APU,
                Label = $"APU / iGPU Only ({map.ApuName})",
                ShortLabel = "APU (680M)",
                CardSubtitle = $"iGPU Vulkan {map.ApuVulkanIndex} · OLLAMA_IGPU_ENABLE",
                Description =
                    $"Vulkan on integrated GPU Vulkan index {map.ApuVulkanIndex} only. Requires system Vulkan loader + OLLAMA_IGPU_ENABLE on Windows.",
                Requires680MWorkaround = true,
                Variables = new Dictionary<string, string>
                {
                    ["OLLAMA_VULKAN"] = "1",
                    ["HIP_VISIBLE_DEVICES"] = "-1",
                    ["GGML_VK_VISIBLE_DEVICES"] = map.ApuVulkanIndex,
                    ["ROCR_VISIBLE_DEVICES"] = "-1",
                    ["OLLAMA_NUM_GPU"] = "999",
                    ["OLLAMA_IGPU_ENABLE"] = "1"
                }
            },
            [ComputeMode.GPU] = new ModeDefinition
            {
                Mode = ComputeMode.GPU,
                Label = $"Discrete GPU Only ({map.GpuName})",
                ShortLabel = "GPU (6700S)",
                CardSubtitle = $"dGPU Vulkan {map.GpuVulkanIndex} · fastest for most models",
                Description = $"Vulkan on discrete GPU Vulkan index {map.GpuVulkanIndex} only.",
                Variables = new Dictionary<string, string>
                {
                    ["OLLAMA_VULKAN"] = "1",
                    ["HIP_VISIBLE_DEVICES"] = "-1",
                    ["GGML_VK_VISIBLE_DEVICES"] = map.GpuVulkanIndex,
                    ["ROCR_VISIBLE_DEVICES"] = "-1",
                    ["OLLAMA_IGPU_ENABLE"] = "0"
                }
            },
            [ComputeMode.Hybrid] = new ModeDefinition
            {
                Mode = ComputeMode.Hybrid,
                Label = "Hybrid (iGPU + dGPU)",
                ShortLabel = "Hybrid",
                CardSubtitle = $"Vulkan {map.HybridVulkanValue} · both GPUs",
                Description =
                    $"Vulkan on both GPUs (Vulkan indices {map.HybridVulkanValue}) for split scheduling.",
                Variables = new Dictionary<string, string>
                {
                    ["OLLAMA_VULKAN"] = "1",
                    ["HIP_VISIBLE_DEVICES"] = "-1",
                    ["GGML_VK_VISIBLE_DEVICES"] = map.HybridVulkanValue,
                    ["ROCR_VISIBLE_DEVICES"] = "-1",
                    ["OLLAMA_IGPU_ENABLE"] = "1"
                }
            },
            [ComputeMode.ROCm] = new ModeDefinition
            {
                Mode = ComputeMode.ROCm,
                Label = $"ROCm / HIP ({map.GpuName})",
                ShortLabel = "ROCm",
                CardSubtitle = "HIP on discrete GPU · Vulkan disabled",
                Description =
                    "AMD ROCm HIP on discrete GPU — Vulkan disabled. Requires ROCm v7 / HIP7 drivers on Windows.",
                Variables = new Dictionary<string, string>
                {
                    ["OLLAMA_VULKAN"] = "0",
                    ["HIP_VISIBLE_DEVICES"] = "0",
                    ["GGML_VK_VISIBLE_DEVICES"] = "-1",
                    ["ROCR_VISIBLE_DEVICES"] = "0",
                    ["OLLAMA_IGPU_ENABLE"] = "0"
                }
            }
        };
    }
}