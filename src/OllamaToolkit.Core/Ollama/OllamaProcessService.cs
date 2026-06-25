using System.Diagnostics;
using OllamaToolkit.Core.Windows;

namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaProcessService
{
    private static readonly string[] ProcessNames = ["ollama", "ollama app"];

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

    public Task StartApplicationAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigPaths.OllamaAppPath))
        {
            throw new FileNotFoundException($"Ollama app not found: {ConfigPaths.OllamaAppPath}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = ConfigPaths.OllamaAppPath,
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    public async Task RestartAsync(
        bool autoStart = true,
        int timeoutSec = 90,
        IntPtr? keepFocusWindow = null,
        CancellationToken cancellationToken = default)
    {
        using var guardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var guardTask = ShouldGuardForeground(keepFocusWindow)
            ? WindowsForeground.GuardForegroundAsync(keepFocusWindow!.Value, guardCts.Token)
            : Task.CompletedTask;

        try
        {
            await StopAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (autoStart)
            {
                await StartApplicationAsync(cancellationToken).ConfigureAwait(false);
            }

            await WaitForApiReadyAsync(timeoutSec, autoStart, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            guardCts.Cancel();
            try
            {
                await guardTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the guard is cancelled.
            }

            if (ShouldGuardForeground(keepFocusWindow))
            {
                WindowsForeground.TryBringToForeground(keepFocusWindow!.Value);
            }
        }
    }

    public async Task WaitForApiReadyAsync(
        int timeoutSec = 90,
        bool autoStart = true,
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

            if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (autoStart && !started && attempt >= 3 && GetProcesses().Count == 0)
            {
                await StartApplicationAsync(cancellationToken).ConfigureAwait(false);
                started = true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Ollama API not ready after {timeoutSec}s.");
    }

    private static bool ShouldGuardForeground(IntPtr? windowHandle) =>
        windowHandle is { } handle && handle != IntPtr.Zero;

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