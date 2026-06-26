using OllamaToolkit.Core.Vulkan;

namespace OllamaToolkit.Core.Modes;

public static class ModeEnvSummaryBuilder
{
    public static string Build(
        string detectedMode,
        IReadOnlyDictionary<string, string?> snapshot,
        VulkanDeviceMap deviceMap,
        ModeDefinitionService definitions)
    {
        var lines = new List<string>();
        if (definitions.TryParse(detectedMode, out var parsed))
        {
            var def = definitions.Get(parsed);
            lines.Add("CURRENT MODE");
            lines.Add($"  {def.Label}");
            lines.Add($"  {def.Description}");
        }
        else
        {
            lines.Add("CURRENT MODE");
            lines.Add($"  {detectedMode} — settings do not match a standard toolkit preset.");
        }

        lines.Add(string.Empty);
        lines.Add("WHAT THIS MEANS");
        lines.AddRange(ExplainBehavior(snapshot, deviceMap, detectedMode));
        lines.Add(string.Empty);
        lines.Add("YOUR SAVED SETTINGS");
        lines.Add("  (Windows user environment — Ollama reads these on startup)");
        lines.Add(string.Empty);

        foreach (var name in ConfigPaths.ManagedEnvironmentVariables)
        {
            snapshot.TryGetValue(name, out var raw);
            var value = string.IsNullOrWhiteSpace(raw) ? "(not set)" : raw!.Trim();
            lines.Add($"  {FormatFriendlyName(name),-22} {value}");
            var hint = DescribeVariable(name, value, deviceMap);
            if (!string.IsNullOrEmpty(hint))
            {
                lines.Add($"    → {hint}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("DETECTED HARDWARE");
        lines.Add($"  Integrated GPU (APU): Vulkan index {deviceMap.ApuVulkanIndex} — {deviceMap.ApuName}");
        lines.Add($"  Discrete GPU:         Vulkan index {deviceMap.GpuVulkanIndex} — {deviceMap.GpuName}");
        lines.Add($"  Hybrid uses both:     Vulkan indices {deviceMap.HybridVulkanValue}");
        lines.Add(string.Empty);
        lines.Add("Tip: Click a mode button above to switch. Enable \"Restart Ollama\" so changes take effect immediately.");

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> ExplainBehavior(
        IReadOnlyDictionary<string, string?> snapshot,
        VulkanDeviceMap map,
        string detectedMode)
    {
        snapshot.TryGetValue("OLLAMA_VULKAN", out var vulkan);
        var vulkanOn = vulkan == "1";

        snapshot.TryGetValue("HIP_VISIBLE_DEVICES", out var hip);
        snapshot.TryGetValue("ROCR_VISIBLE_DEVICES", out var rocr);
        var rocmActive = hip is "0" && rocr is "0";

        if (rocmActive || detectedMode.Equals("ROCm", StringComparison.OrdinalIgnoreCase))
        {
            yield return "  • AMD ROCm / HIP path is active — Vulkan is off.";
            yield return $"  • Discrete GPU via ROCm (HIP device 0): {map.GpuName}.";
            yield return "  • Requires ROCm v7 / HIP7-capable AMD drivers on Windows.";
            yield return $"  • Integrated GPU ({map.ApuName}) is not used with ROCm on Windows — use APU mode for iGPU.";
            yield break;
        }

        if (!vulkanOn)
        {
            yield return "  • Inference runs on CPU only — no GPU acceleration.";
            yield return "  • Best for troubleshooting or when GPU modes fail.";
            yield break;
        }

        snapshot.TryGetValue("GGML_VK_VISIBLE_DEVICES", out var vkDevices);
        snapshot.TryGetValue("OLLAMA_IGPU_ENABLE", out var igpu);
        var devices = vkDevices ?? "(not set)";

        yield return "  • Vulkan GPU acceleration is ON.";
        yield return ExplainGpuSelection(devices, igpu, map, detectedMode);
        yield return "  • AMD ROCm / HIP is disabled (Vulkan used instead).";
    }

    private static string ExplainGpuSelection(
        string devices,
        string? igpu,
        VulkanDeviceMap map,
        string detectedMode)
    {
        if (devices == "-1" || devices.Equals("(not set)", StringComparison.OrdinalIgnoreCase))
        {
            return "  • No Vulkan GPU selected.";
        }

        if (devices == map.ApuVulkanIndex || detectedMode.Equals("APU", StringComparison.OrdinalIgnoreCase))
        {
            return $"  • Using integrated GPU only: {map.ApuName} (Vulkan index {map.ApuVulkanIndex}).";
        }

        if (devices == map.GpuVulkanIndex || detectedMode.Equals("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return $"  • Using discrete GPU only: {map.GpuName} (Vulkan index {map.GpuVulkanIndex}).";
        }

        if (devices.Contains(',') || detectedMode.Equals("Hybrid", StringComparison.OrdinalIgnoreCase))
        {
            return $"  • Using both GPUs: {map.ApuName} + {map.GpuName} (Vulkan indices {map.HybridVulkanValue}).";
        }

        return $"  • Vulkan device index(es): {devices}.";
    }

    private static string FormatFriendlyName(string name) => name switch
    {
        "OLLAMA_VULKAN" => "Vulkan",
        "GGML_VK_VISIBLE_DEVICES" => "Vulkan GPU(s)",
        "OLLAMA_IGPU_ENABLE" => "Integrated GPU",
        "OLLAMA_NUM_GPU" => "GPU layers",
        "OLLAMA_NUM_PARALLEL" => "Parallel slots",
        "HIP_VISIBLE_DEVICES" => "AMD HIP",
        "ROCR_VISIBLE_DEVICES" => "AMD ROCm",
        "CUDA_VISIBLE_DEVICES" => "NVIDIA CUDA",
        _ => name
    };

    private static string DescribeVariable(string name, string value, VulkanDeviceMap map) => name switch
    {
        "OLLAMA_VULKAN" => value == "1" ? "Use Vulkan for GPU inference" : "CPU-only inference",
        "GGML_VK_VISIBLE_DEVICES" => DescribeVkDevices(value, map),
        "OLLAMA_IGPU_ENABLE" => value == "1" ? "Allow integrated (APU) GPU" : "Discrete GPU only",
        "OLLAMA_NUM_GPU" => value == "(not set)" ? "Ollama default layer offload" : $"Offload up to {value} GPU layers",
        "OLLAMA_NUM_PARALLEL" => value == "(not set)"
            ? "Ollama default (typically 1 concurrent model slot)"
            : $"{value} concurrent model slot(s) — multiplies VRAM reservation per loaded model",
        "HIP_VISIBLE_DEVICES" =>
            value == "-1" ? "HIP disabled (Vulkan path)" : value == "0" ? "HIP device 0 (discrete GPU)" : $"Set to {value}",
        "ROCR_VISIBLE_DEVICES" =>
            value == "-1" ? "ROCm disabled (Vulkan path)" : value == "0" ? "ROCm device 0 (discrete GPU)" : $"Set to {value}",
        "CUDA_VISIBLE_DEVICES" =>
            value == "(not set)" ? "Not configured (no NVIDIA path)" : $"Set to {value}",
        _ => string.Empty
    };

    private static string DescribeVkDevices(string value, VulkanDeviceMap map)
    {
        if (value == "-1")
        {
            return "No GPU — CPU fallback";
        }

        if (value == map.ApuVulkanIndex)
        {
            return $"APU / iGPU only ({map.ApuName})";
        }

        if (value == map.GpuVulkanIndex)
        {
            return $"Discrete GPU only ({map.GpuName})";
        }

        if (value == map.HybridVulkanValue || value.Contains(','))
        {
            return $"Both GPUs ({map.ApuName} + {map.GpuName})";
        }

        return $"Vulkan device index {value}";
    }
}