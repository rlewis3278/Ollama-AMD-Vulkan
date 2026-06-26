using OllamaToolkit.Core.Vulkan;

namespace OllamaToolkit.Core.SystemMonitor;

public sealed class WindowsSystemMonitorSampler : IDisposable
{
    private readonly CpuUsageReader _cpu = new();
    private readonly GpuPerformanceCounterRegistry _gpuCounters = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private VulkanDeviceMap? _deviceMap;
    private SystemMonitorSnapshot? _previous;

    public event Action<SystemMonitorSnapshot>? SnapshotReady;

    public void Configure(VulkanDeviceMap deviceMap)
    {
        lock (_gate)
        {
            _deviceMap = deviceMap;
            var mapped = GpuAdapterRoleMapper.Map(deviceMap);
            _gpuCounters.Initialize(mapped);
            _gpuCounters.Prime();
            _previous = null;
        }
    }

    public void Start(TimeSpan interval)
    {
        lock (_gate)
        {
            StopCore();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunLoopAsync(interval, token), token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        Stop();
        _gpuCounters.Dispose();
    }

    private void StopCore()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best effort shutdown.
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = Sample();
            if (!HasMeaningfulChange(snapshot))
            {
                continue;
            }

            _previous = snapshot;
            SnapshotReady?.Invoke(snapshot);
        }
    }

    private SystemMonitorSnapshot Sample()
    {
        var map = _deviceMap;
        var apuName = map?.ApuName ?? "APU";
        var gpuName = map?.GpuName ?? "GPU";

        double? memoryUsed = null;
        double? memoryTotal = null;
        var memory = NativeMemoryStatus.TryReadPhysicalMemoryGb();
        if (memory is not null)
        {
            memoryUsed = memory.Value.UsedGb;
            memoryTotal = memory.Value.TotalGb;
        }

        return new SystemMonitorSnapshot(
            _cpu.SamplePercent(),
            memoryUsed,
            memoryTotal,
            _gpuCounters.Read(AdapterRole.Apu, apuName),
            _gpuCounters.Read(AdapterRole.Gpu, gpuName));
    }

    private bool HasMeaningfulChange(SystemMonitorSnapshot snapshot)
    {
        var previous = _previous;
        if (previous is null)
        {
            return true;
        }

        if (!ApproximatelyEqual(previous.CpuPercent, snapshot.CpuPercent, 0.5))
        {
            return true;
        }

        if (!ApproximatelyEqual(previous.MemoryUsedGb, snapshot.MemoryUsedGb, 0.05))
        {
            return true;
        }

        if (!AdapterApproximatelyEqual(previous.Apu, snapshot.Apu))
        {
            return true;
        }

        return !AdapterApproximatelyEqual(previous.Gpu, snapshot.Gpu);
    }

    private static bool AdapterApproximatelyEqual(AdapterMonitorReading left, AdapterMonitorReading right)
    {
        if (left.IsAvailable != right.IsAvailable)
        {
            return false;
        }

        return ApproximatelyEqual(left.UtilizationPercent, right.UtilizationPercent, 1.0)
            && ApproximatelyEqual(left.DedicatedUsedGb, right.DedicatedUsedGb, 0.05)
            && ApproximatelyEqual(left.SharedUsedGb, right.SharedUsedGb, 0.05)
            && ApproximatelyEqual(left.Compute0Percent, right.Compute0Percent, 1.0);
    }

    private static bool ApproximatelyEqual(double? left, double? right, double epsilon)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return Math.Abs(left.Value - right.Value) <= epsilon;
    }
}
