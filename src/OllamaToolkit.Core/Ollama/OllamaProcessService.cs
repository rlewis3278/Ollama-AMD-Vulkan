using System.Diagnostics;

namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaProcessService
{
    private static readonly string[] ProcessNames = ["ollama", "ollama app"];
    private Process? _serveProcess;

    public async Task StopAsync(bool quick = false, int timeoutSec = 45, CancellationToken cancellationToken = default)
    {
        if (quick)
        {
            timeoutSec = Math.Min(timeoutSec, 5);
        }

        StopServeProcess();

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
        cancellationToken.ThrowIfCancellationRequested();
        var appPath = ResolveOllamaAppPath();
        if (appPath is null)
        {
            throw new FileNotFoundException(
                $"Ollama tray app not found. Expected under {ConfigPaths.OllamaAppPath}.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = false
        };
        ApplyManagedEnvToStartInfo(startInfo);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Ollama tray application.");

        return Task.CompletedTask;
    }

    public Task StartServeProcessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var servePath = ResolveOllamaServePath();
        if (servePath is null)
        {
            throw new FileNotFoundException(
                $"Ollama serve executable not found. Expected under {ConfigPaths.OllamaServeExePath}.");
        }

        StopServeProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = servePath,
            Arguments = "serve",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        ApplyManagedEnvToStartInfo(startInfo);
        _serveProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ollama serve.");

        return Task.CompletedTask;
    }

    public async Task RestartAsync(
        bool autoStart = true,
        int timeoutSec = 45,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(quick: true, timeoutSec: 15, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (autoStart)
        {
            // Tray restart inherits managed env from this process and writes server.log.
            await StartApplicationAsync(cancellationToken).ConfigureAwait(false);
            await WaitForApiReadyAsync(timeoutSec, autoStart: false, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForApiReadyAsync(
        int timeoutSec = 90,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        var result = await EnsureApiReadyAsync(timeoutSec, cancellationToken, autoStart).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new TimeoutException(result.Message);
        }
    }

    public async Task<OllamaStartupResult> EnsureApiReadyAsync(
        int timeoutSec = 90,
        CancellationToken cancellationToken = default,
        bool autoStart = true)
    {
        using var client = new OllamaApiClient();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        var attempt = 0;
        var startedTray = false;
        var startedServe = false;
        string? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    if (await client.IsReadyAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return new OllamaStartupResult
                        {
                            Success = true,
                            Message = "Ollama API is ready.",
                            StartedTrayApp = startedTray,
                            StartedServeProcess = startedServe
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (!autoStart)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!startedTray && attempt >= 2 && !HasManagedServeProcess)
            {
                try
                {
                    await StartApplicationAsync(cancellationToken).ConfigureAwait(false);
                    startedTray = true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            if (!startedServe && attempt >= 10 && GetProcesses().Count == 0)
            {
                try
                {
                    await StartServeProcessAsync(cancellationToken).ConfigureAwait(false);
                    startedServe = true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var detail = string.IsNullOrWhiteSpace(lastError)
            ? "Start the Ollama app from the system tray or reinstall Ollama."
            : lastError;
        return new OllamaStartupResult
        {
            Success = false,
            Message = $"Ollama API not ready after {timeoutSec}s. {detail}",
            StartedTrayApp = startedTray,
            StartedServeProcess = startedServe
        };
    }

    private void StopServeProcess()
    {
        if (_serveProcess is null)
        {
            return;
        }

        try
        {
            if (!_serveProcess.HasExited)
            {
                _serveProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            _serveProcess.Dispose();
            _serveProcess = null;
        }
    }

    private static string? ResolveOllamaAppPath()
    {
        foreach (var path in ConfigPaths.OllamaAppCandidates())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? ResolveOllamaServePath()
    {
        foreach (var path in ConfigPaths.OllamaServeCandidates())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
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

    internal static void ApplyManagedEnvToStartInfo(ProcessStartInfo startInfo)
    {
        foreach (var key in System.Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process).Keys
                     .Cast<string>())
        {
            var value = System.Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[key] = value;
            }
        }

        foreach (var name in ConfigPaths.ManagedEnvironmentVariables)
        {
            var value = ResolveManagedEnvironmentValue(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }
    }

    internal static string? ResolveManagedEnvironmentValue(string name)
    {
        var processValue = System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue;
        }

        return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
    }

    public bool HasManagedServeProcess =>
        _serveProcess is { HasExited: false };
}