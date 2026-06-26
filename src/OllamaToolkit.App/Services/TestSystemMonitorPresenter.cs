using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OllamaToolkit.Core.SystemMonitor;
using OllamaToolkit.Core.Vulkan;

namespace OllamaToolkit.App.Services;

public sealed class TestSystemMonitorPresenter : IDisposable
{
    private readonly TextBlock _text;
    private readonly Dispatcher _dispatcher;
    private readonly WindowsSystemMonitorSampler _sampler = new();
    private bool _started;

    public TestSystemMonitorPresenter(TextBlock text, VulkanDeviceMap deviceMap, Dispatcher? dispatcher = null)
    {
        _text = text;
        _dispatcher = dispatcher ?? text.Dispatcher;
        _sampler.Configure(deviceMap);
        _sampler.SnapshotReady += OnSnapshotReady;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _sampler.Start(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _sampler.Stop();
    }

    public void Dispose()
    {
        Stop();
        _sampler.SnapshotReady -= OnSnapshotReady;
        _sampler.Dispose();
    }

    private void OnSnapshotReady(SystemMonitorSnapshot snapshot)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnSnapshotReady(snapshot));
            return;
        }

        _text.Text = Format(snapshot);
    }

    private static string Format(SystemMonitorSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CPU       {FormatPercent(snapshot.CpuPercent)}");
        sb.AppendLine($"Memory    {FormatMemory(snapshot.MemoryUsedGb, snapshot.MemoryTotalGb)}");
        sb.AppendLine();
        AppendAdapter(sb, "APU", snapshot.Apu);
        sb.AppendLine();
        AppendAdapter(sb, "GPU", snapshot.Gpu);
        return sb.ToString().TrimEnd();
    }

    private static void AppendAdapter(StringBuilder sb, string prefix, AdapterMonitorReading reading)
    {
        sb.AppendLine($"{prefix} ({reading.DisplayName})");
        if (!reading.IsAvailable)
        {
            sb.AppendLine("  N/A");
            return;
        }

        sb.AppendLine($"  Utilization  {FormatPercent(reading.UtilizationPercent)}");
        sb.AppendLine($"  Dedicated    {FormatMemory(reading.DedicatedUsedGb, reading.DedicatedTotalGb)}");
        sb.AppendLine($"  Shared       {FormatMemory(reading.SharedUsedGb, reading.SharedTotalGb)}");
        sb.AppendLine($"  Compute 0    {FormatPercent(reading.Compute0Percent)}");
    }

    private static string FormatPercent(double? value) =>
        value is null ? "N/A" : $"{value.Value:0}%";

    private static string FormatMemory(double? usedGb, double? totalGb)
    {
        if (usedGb is null || totalGb is null)
        {
            return "N/A";
        }

        return $"{usedGb.Value:0.0} / {totalGb.Value:0.0} GB";
    }
}
