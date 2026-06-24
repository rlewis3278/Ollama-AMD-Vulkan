using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OllamaToolkit.Core.Vulkan;

public sealed class VulkanDeviceDiscovery
{
    private static readonly Regex GpuHeaderRegex = new(@"^GPU(\d+):", RegexOptions.Compiled);

    public VulkanDeviceMap Discover(string? vulkanInfoPath = null)
    {
        var exe = ResolveVulkanInfoPath(vulkanInfoPath);
        var devices = ParseVulkanInfo(exe);
        return BuildDeviceMap(devices);
    }

    public static string ResolveVulkanInfoPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var pathEnv = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(folder, "vulkaninfo.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var system32 = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "vulkaninfo.exe");
        if (File.Exists(system32))
        {
            return system32;
        }

        throw new FileNotFoundException("vulkaninfo.exe not found. Install AMD drivers or the Vulkan SDK.");
    }

    private static IReadOnlyList<VulkanDeviceInfo> ParseVulkanInfo(string exe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{exe}\" --summary 2>nul\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start vulkaninfo.");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("vulkaninfo returned no output.");
        }

        var devices = new List<VulkanDeviceInfo>();
        VulkanDeviceInfo? current = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var headerMatch = GpuHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                if (current is not null)
                {
                    devices.Add(current);
                }

                current = new VulkanDeviceInfo { Index = int.Parse(headerMatch.Groups[1].Value) };
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (TryReadField(line, "deviceName", out var name))
            {
                current.Name = name;
            }
            else if (TryReadField(line, "deviceType", out var deviceType))
            {
                current.DeviceType = deviceType;
            }
        }

        if (current is not null)
        {
            devices.Add(current);
        }

        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No Vulkan devices parsed from vulkaninfo output.");
        }

        return devices;
    }

    private static bool TryReadField(string line, string key, out string value)
    {
        var prefix = $"{key} =";
        if (!line.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = line.Split('=', 2)[1].Trim();
        return true;
    }

    private static VulkanDeviceMap BuildDeviceMap(IReadOnlyList<VulkanDeviceInfo> devices)
    {
        var integrated = devices.FirstOrDefault(d => d.DeviceType.Contains("INTEGRATED", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Name.Contains("680M", StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase));

        var discrete = devices.FirstOrDefault(d => d.DeviceType.Contains("DISCRETE", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Name.Contains("6700", StringComparison.OrdinalIgnoreCase));

        if (integrated is null || discrete is null)
        {
            throw new InvalidOperationException("Could not identify both integrated and discrete Vulkan devices.");
        }

        var hybrid = string.Join(',', devices.Select(d => d.Index).OrderBy(i => i));

        return new VulkanDeviceMap
        {
            ApuVulkanIndex = integrated.Index.ToString(),
            GpuVulkanIndex = discrete.Index.ToString(),
            HybridVulkanValue = hybrid,
            ApuName = integrated.Name,
            GpuName = discrete.Name
        };
    }

    private sealed class VulkanDeviceInfo
    {
        public int Index { get; init; }
        public string Name { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
    }
}