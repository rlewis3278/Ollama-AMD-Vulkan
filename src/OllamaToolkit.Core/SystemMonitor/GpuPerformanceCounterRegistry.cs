using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OllamaToolkit.Core.SystemMonitor;

internal sealed class GpuPerformanceCounterRegistry : IDisposable
{
    private static readonly Regex ComputeEngineRegex = new(
        @"engtype_Compute.*eng_0\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Dictionary<AdapterRole, AdapterCounterSet> _sets = new();
    private bool _primed;

    public void Initialize(IReadOnlyList<MappedAdapter> adapters)
    {
        DisposeCounters();
        _sets.Clear();
        _primed = false;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var memoryInstances = GetInstancesSafe("GPU Adapter Memory");
            var engineInstances = GetInstancesSafe("GPU Engine");
            foreach (var adapter in adapters)
            {
                var luidKey = GpuAdapterRoleMapper.FormatLuidKey(adapter.LuidHigh, adapter.LuidLow);
                var memoryInstance = memoryInstances.FirstOrDefault(i =>
                    i.Contains(luidKey, StringComparison.OrdinalIgnoreCase));
                if (memoryInstance is null)
                {
                    continue;
                }

                var engineMatches = engineInstances
                    .Where(i => i.Contains(luidKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                _sets[adapter.Role] = new AdapterCounterSet(
                    adapter.Role,
                    adapter.DisplayName,
                    CreateCounter("GPU Adapter Memory", memoryInstance, "Dedicated Usage"),
                    CreateCounter("GPU Adapter Memory", memoryInstance, "Dedicated Limit"),
                    CreateCounter("GPU Adapter Memory", memoryInstance, "Shared Usage"),
                    CreateCounter("GPU Adapter Memory", memoryInstance, "Shared Limit"),
                    engineMatches
                        .Select(i => CreateCounter("GPU Engine", i, "Utilization Percentage"))
                        .Where(c => c is not null)
                        .Cast<PerformanceCounter>()
                        .ToList(),
                    engineMatches
                        .Where(i => ComputeEngineRegex.IsMatch(i))
                        .Select(i => CreateCounter("GPU Engine", i, "Utilization Percentage"))
                        .Where(c => c is not null)
                        .Cast<PerformanceCounter>()
                        .FirstOrDefault());
            }
        }
        catch
        {
            DisposeCounters();
            _sets.Clear();
        }
    }

    public AdapterMonitorReading Read(AdapterRole role, string displayName)
    {
        if (!_sets.TryGetValue(role, out var set))
        {
            return AdapterMonitorReading.Unavailable(role, displayName);
        }

        try
        {
            var dedicatedUsed = ReadBytes(set.DedicatedUsage);
            var dedicatedTotal = ReadBytes(set.DedicatedLimit);
            var sharedUsed = ReadBytes(set.SharedUsage);
            var sharedTotal = ReadBytes(set.SharedLimit);

            var utilization = ReadMaxPercent(set.UtilizationCounters);
            var compute0 = set.Compute0Counter is null ? (double?)null : ReadPercent(set.Compute0Counter);

            return new AdapterMonitorReading(
                role,
                displayName,
                true,
                utilization,
                BytesToGb(dedicatedUsed),
                BytesToGb(dedicatedTotal),
                BytesToGb(sharedUsed),
                BytesToGb(sharedTotal),
                compute0);
        }
        catch
        {
            return AdapterMonitorReading.Unavailable(role, displayName);
        }
    }

    public void Prime()
    {
        if (_primed)
        {
            return;
        }

        foreach (var set in _sets.Values)
        {
            _ = ReadBytes(set.DedicatedUsage);
            _ = ReadBytes(set.DedicatedLimit);
            _ = ReadBytes(set.SharedUsage);
            _ = ReadBytes(set.SharedLimit);
            ReadMaxPercent(set.UtilizationCounters);
            if (set.Compute0Counter is not null)
            {
                _ = set.Compute0Counter.NextValue();
            }
        }

        _primed = true;
    }

    public void Dispose() => DisposeCounters();

    private void DisposeCounters()
    {
        foreach (var set in _sets.Values)
        {
            set.Dispose();
        }
    }

    private static IReadOnlyList<string> GetInstancesSafe(string category)
    {
        try
        {
            return new PerformanceCounterCategory(category).GetInstanceNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static PerformanceCounter? CreateCounter(string category, string instance, string counter)
    {
        try
        {
            return new PerformanceCounter(category, counter, instance, readOnly: true);
        }
        catch
        {
            return null;
        }
    }

    private static float ReadBytes(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return 0;
        }

        return Math.Max(0, counter.NextValue());
    }

    private static double? ReadPercent(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return null;
        }

        return Math.Clamp(counter.NextValue(), 0, 100);
    }

    private static double? ReadMaxPercent(IReadOnlyList<PerformanceCounter> counters)
    {
        if (counters.Count == 0)
        {
            return null;
        }

        double? max = null;
        foreach (var counter in counters)
        {
            var value = Math.Clamp(counter.NextValue(), 0, 100);
            max = max is null ? value : Math.Max(max.Value, value);
        }

        return max;
    }

    private static double? BytesToGb(float bytes) =>
        bytes <= 0 ? null : bytes / (1024f * 1024f * 1024f);

    private sealed class AdapterCounterSet : IDisposable
    {
        public AdapterCounterSet(
            AdapterRole role,
            string displayName,
            PerformanceCounter? dedicatedUsage,
            PerformanceCounter? dedicatedLimit,
            PerformanceCounter? sharedUsage,
            PerformanceCounter? sharedLimit,
            IReadOnlyList<PerformanceCounter> utilizationCounters,
            PerformanceCounter? compute0Counter)
        {
            Role = role;
            DisplayName = displayName;
            DedicatedUsage = dedicatedUsage;
            DedicatedLimit = dedicatedLimit;
            SharedUsage = sharedUsage;
            SharedLimit = sharedLimit;
            UtilizationCounters = utilizationCounters;
            Compute0Counter = compute0Counter;
        }

        public AdapterRole Role { get; }
        public string DisplayName { get; }
        public PerformanceCounter? DedicatedUsage { get; }
        public PerformanceCounter? DedicatedLimit { get; }
        public PerformanceCounter? SharedUsage { get; }
        public PerformanceCounter? SharedLimit { get; }
        public IReadOnlyList<PerformanceCounter> UtilizationCounters { get; }
        public PerformanceCounter? Compute0Counter { get; }

        public void Dispose()
        {
            DedicatedUsage?.Dispose();
            DedicatedLimit?.Dispose();
            SharedUsage?.Dispose();
            SharedLimit?.Dispose();
            foreach (var counter in UtilizationCounters)
            {
                counter.Dispose();
            }

            Compute0Counter?.Dispose();
        }
    }
}

