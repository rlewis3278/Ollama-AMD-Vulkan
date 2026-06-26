using System.Text.RegularExpressions;
using OllamaToolkit.Core.Vulkan;

namespace OllamaToolkit.Core.SystemMonitor;

internal sealed record MappedAdapter(
    AdapterRole Role,
    string DisplayName,
    uint LuidHigh,
    uint LuidLow);

internal static class GpuAdapterRoleMapper
{
    private static readonly Regex LuidRegex = new(
        @"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<MappedAdapter> Map(VulkanDeviceMap deviceMap)
    {
        var dxgiAdapters = DxgiAdapterEnumeration.Enumerate();
        var mapped = new List<MappedAdapter>();

        TryMapRole(AdapterRole.Apu, deviceMap.ApuName, dxgiAdapters, mapped);
        TryMapRole(AdapterRole.Gpu, deviceMap.GpuName, dxgiAdapters, mapped);
        return mapped;
    }

    public static string FormatLuidKey(uint luidHigh, uint luidLow) =>
        $"luid_0x{luidHigh:x8}_0x{luidLow:x8}";

    public static bool TryParseLuidFromInstance(string instanceName, out uint luidHigh, out uint luidLow)
    {
        luidHigh = 0;
        luidLow = 0;
        var match = LuidRegex.Match(instanceName);
        if (!match.Success)
        {
            return false;
        }

        luidHigh = Convert.ToUInt32(match.Groups[1].Value, 16);
        luidLow = Convert.ToUInt32(match.Groups[2].Value, 16);
        return true;
    }

    private static void TryMapRole(
        AdapterRole role,
        string expectedName,
        IReadOnlyList<DxgiAdapterInfo> adapters,
        ICollection<MappedAdapter> mapped)
    {
        foreach (var adapter in adapters)
        {
            if (!AdapterNameMatcher.NamesMatch(adapter.Description, expectedName))
            {
                continue;
            }

            mapped.Add(new MappedAdapter(role, expectedName, adapter.AdapterLuidHigh, adapter.AdapterLuidLow));
            return;
        }
    }
}

internal static class AdapterNameMatcher
{
    public static bool NamesMatch(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return true;
        }

        var tokensA = SignificantTokens(a);
        var tokensB = SignificantTokens(b);
        return tokensA.Overlaps(tokensB);
    }

    private static string Normalize(string value) =>
        new string(value.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();

    private static HashSet<string> SignificantTokens(string normalized)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in normalized.Split(new[] { "radeon", "amd", "graphics", "tm" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 3)
            {
                tokens.Add(part);
            }
        }

        if (normalized.Contains("680m", StringComparison.Ordinal))
        {
            tokens.Add("680m");
        }

        if (normalized.Contains("6700", StringComparison.Ordinal))
        {
            tokens.Add("6700");
        }

        return tokens;
    }
}

