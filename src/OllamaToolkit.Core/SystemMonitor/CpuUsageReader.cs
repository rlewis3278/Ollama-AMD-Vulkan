using System.Runtime.InteropServices;

namespace OllamaToolkit.Core.SystemMonitor;

internal sealed class CpuUsageReader
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasBaseline;

    public double? SamplePercent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var idleTicks = FileTimeToUInt64(idle);
        var kernelTicks = FileTimeToUInt64(kernel);
        var userTicks = FileTimeToUInt64(user);

        if (!_hasBaseline)
        {
            _previousIdle = idleTicks;
            _previousKernel = kernelTicks;
            _previousUser = userTicks;
            _hasBaseline = true;
            return null;
        }

        var idleDelta = idleTicks - _previousIdle;
        var kernelDelta = kernelTicks - _previousKernel;
        var userDelta = userTicks - _previousUser;
        _previousIdle = idleTicks;
        _previousKernel = kernelTicks;
        _previousUser = userTicks;

        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return null;
        }

        var busy = total - idleDelta;
        if (busy < 0)
        {
            busy = 0;
        }

        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    private static ulong FileTimeToUInt64(FILETIME time) =>
        ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }
}
