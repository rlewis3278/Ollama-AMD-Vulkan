namespace OllamaToolkit.Core.SystemMonitor;

public enum AdapterRole
{
    Apu,
    Gpu
}

public sealed record AdapterMonitorReading(
    AdapterRole Role,
    string DisplayName,
    bool IsAvailable,
    double? UtilizationPercent,
    double? DedicatedUsedGb,
    double? DedicatedTotalGb,
    double? SharedUsedGb,
    double? SharedTotalGb,
    double? Compute0Percent)
{
    public static AdapterMonitorReading Unavailable(AdapterRole role, string displayName) =>
        new(role, displayName, false, null, null, null, null, null, null);
}

public sealed record SystemMonitorSnapshot(
    double? CpuPercent,
    double? MemoryUsedGb,
    double? MemoryTotalGb,
    AdapterMonitorReading Apu,
    AdapterMonitorReading Gpu);