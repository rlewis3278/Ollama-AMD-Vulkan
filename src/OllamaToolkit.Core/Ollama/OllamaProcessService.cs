using System.Diagnostics;
using OllamaToolkit.Core.Windows;

namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaProcessService
{
    private static readonly string[] ProcessNames = ["ollama", "ollama app"];
    private static readonly TimeSpan ForegroundGuardDuration = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ForegroundGuardInterval = TimeSpan.FromMilliseconds(400);

    public async Task StopAsync(bool quick = false, int timeoutSec = 45, CancellationToken cancellationToken = default)
    {
        if (quick)
        {
            timeoutSec = Math.Min(timeoutSec, 5);
        }

        foreach (var process in GetProcesses())
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort during shutdown.
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetProcesses().Count == 0)
            {
                if (!quick)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await Task.Delay(quick ? 150 : 500, cancellationToken).ConfigureAwait(false);
        }

        if (!quick)
        {
            throw new TimeoutException("Timed out waiting for Ollama processes to exit.");
        }
    }

    public Task StartApplicationAsync(
        IntPtr? restoreFocusWindow = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPaths.OllamaAppPath))
        {
            throw new FileNotFoundException($"Ollama app not found: {ConfigPaths.OllamaAppPath}");
        }

        WindowsNative.StartProcessMinimizedNoActivate(ConfigPaths.OllamaAppPath);
        _ = GuardForegroundDuringStartupAsync(restoreFocusWindow, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task RestartAsync(
        bool autoStart = true,
        int timeoutSec = 90,
        IntPtr? restoreFocusWindow = null,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (autoStart)
        {
            await StartApplicationAsync(restoreFocusWindow, cancellationToken).ConfigureAwait(false);
        }

        await WaitForApiReadyAsync(timeoutSec, autoStart, restoreFocusWindow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WaitForApiReadyAsync(
        int timeoutSec = 90,
        bool autoStart = true,
        IntPtr? restoreFocusWindow = null,
        CancellationToken cancellationToken = default)
    {
        var client = new OllamaApiClient();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        var attempt = 0;
        var started = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            WindowsNative.MinimizeOllamaWindows();
            if (restoreFocusWindow is { } handle && handle != IntPtr.Zero)
            {
                WindowsNative.TryRestoreForeground(handle);
            }

            if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                WindowsNative.MinimizeOllamaWindows();
                if (restoreFocusWindow is { } readyHandle && readyHandle != IntPtr.Zero)
                {
                    WindowsNative.TryRestoreForeground(readyHandle);
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (autoStart && !started && attempt >= 3 && GetProcesses().Count == 0)
            {
                await StartApplicationAsync(restoreFocusWindow, cancellationToken).ConfigureAwait(false);
                started = true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Ollama API not ready after {timeoutSec}s.");
    }

    private static async Task GuardForegroundDuringStartupAsync(
        IntPtr? restoreFocusWindow,
        CancellationToken cancellationToken)
    {
        try
        {
            await WindowsNative.GuardForegroundAsync(
                restoreFocusWindow,
                ForegroundGuardDuration,
                ForegroundGuardInterval,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the caller cancels.
        }
    }

    private static List<Process> GetProcesses()
    {
        var list = new List<Process>();
        foreach (var name in ProcessNames)
        {
            list.AddRange(Process.GetProcessesByName(name));
        }

        return list;
    }
}