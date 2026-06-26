using System.Runtime.InteropServices;

namespace OllamaToolkit.Core.SystemMonitor;

internal static class NativeMemoryStatus
{
    public static (double UsedGb, double TotalGb)? TryReadPhysicalMemoryGb()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        var totalGb = BytesToGb(status.ullTotalPhys);
        var usedGb = BytesToGb(status.ullTotalPhys - status.ullAvailPhys);
        return (usedGb, totalGb);
    }

    private static double BytesToGb(ulong bytes) => bytes / (1024.0 * 1024.0 * 1024.0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
