using System.Diagnostics;
using System.Text;

namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaCliService
{
    public string? ResolveOllamaExe()
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

    public async Task<OllamaCliResult> ExecuteAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var exe = ResolveOllamaExe()
            ?? throw new FileNotFoundException("ollama.exe not found. Install Ollama from https://ollama.com");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', args.Select(QuoteArg)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new OllamaCliResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString().Trim(),
            StandardError = stderr.ToString().Trim()
        };
    }

    public Task<OllamaCliResult> RunAsync(
        string model,
        string? prompt = null,
        string keepAlive = "30m",
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "run", model, "--keepalive", keepAlive };
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            args.Add(prompt);
        }

        return ExecuteAsync(args, cancellationToken);
    }

    public Task<OllamaCliResult> StopAsync(string model, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new[] { "stop", model }, cancellationToken);

    public Task<OllamaCliResult> RemoveAsync(string model, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new[] { "rm", model }, cancellationToken);

    public Task<OllamaCliResult> PullAsync(string model, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new[] { "pull", model }, cancellationToken);

    public Task<OllamaCliResult> ListAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new[] { "list" }, cancellationToken);

    public Task<OllamaCliResult> PsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(new[] { "ps" }, cancellationToken);

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? $"\"{arg.Replace("\"", "\\\"")}\""
            : arg;
}