using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OllamaToolkit.Core.Windows;

internal static class WindowsNative
{
    private const int SwMinimize = 6;

    private static readonly string[] OllamaProcessNames = ["ollama", "ollama app"];

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static void StartProcessMinimizedNoActivate(string filePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /min \"{filePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void MinimizeOllamaWindows()
    {
        var pids = GetOllamaProcessIds();
        if (pids.Count == 0)
        {
            return;
        }

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid))
            {
                return true;
            }

            var title = new StringBuilder(256);
            _ = GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0)
            {
                return true;
            }

            ShowWindow(hwnd, SwMinimize);
            return true;
        }, IntPtr.Zero);
    }

    public static void TryRestoreForeground(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        AllowSetForegroundWindow(Process.GetCurrentProcess().Id);
        var foreground = GetForegroundWindow();
        if (foreground != windowHandle)
        {
            SetForegroundWindow(windowHandle);
        }
    }

    public static async Task GuardForegroundAsync(
        IntPtr? windowHandle,
        TimeSpan duration,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.Add(duration);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinimizeOllamaWindows();
            if (windowHandle is { } handle && handle != IntPtr.Zero)
            {
                TryRestoreForeground(handle);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HashSet<int> GetOllamaProcessIds()
    {
        var pids = new HashSet<int>();
        foreach (var name in OllamaProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                pids.Add(process.Id);
            }
        }

        return pids;
    }
}