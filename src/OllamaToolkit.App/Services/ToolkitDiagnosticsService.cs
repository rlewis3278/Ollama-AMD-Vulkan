using System.IO;
using OllamaToolkit.Core;

namespace OllamaToolkit.App.Services;

public sealed class ToolkitDiagnosticsService
{
    private readonly object _lock = new();

    public void Write(string category, string message)
    {
        ConfigPaths.EnsureConfigDirectory();
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            File.AppendAllText(ConfigPaths.ToolkitDiagnosticsLog, line);
        }
    }

    public string ReadTail(int maxLines = 500)
    {
        if (!File.Exists(ConfigPaths.ToolkitDiagnosticsLog))
        {
            return string.Empty;
        }

        lock (_lock)
        {
            var lines = File.ReadAllLines(ConfigPaths.ToolkitDiagnosticsLog);
            return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
    }

    public string ReadAll()
    {
        if (!File.Exists(ConfigPaths.ToolkitDiagnosticsLog))
        {
            return string.Empty;
        }

        lock (_lock)
        {
            return File.ReadAllText(ConfigPaths.ToolkitDiagnosticsLog);
        }
    }
}