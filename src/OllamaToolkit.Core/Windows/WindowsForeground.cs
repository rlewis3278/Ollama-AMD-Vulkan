using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OllamaToolkit.Core.Windows;

internal static class WindowsForeground
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void TryBringToForeground(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        AllowSetForegroundWindow(Process.GetCurrentProcess().Id);
        ShowWindow(windowHandle, SwRestore);
        SetForegroundWindow(windowHandle);
    }

    public static async Task GuardForegroundAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TryBringToForeground(windowHandle);
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Guard ends when mode restart completes or is cancelled.
        }
    }
}