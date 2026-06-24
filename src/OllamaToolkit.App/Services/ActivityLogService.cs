using System.IO;
using OllamaToolkit.Core;

namespace OllamaToolkit.App.Services;

public sealed class ActivityLogService
{
    private readonly object _lock = new();

    public void Write(string category, string message)
    {
        ConfigPaths.EnsureConfigDirectory();
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            File.AppendAllText(ConfigPaths.GuiAiActivityLog, line);
        }
    }

    public string ReadTail(int maxLines = 200)
    {
        if (!File.Exists(ConfigPaths.GuiAiActivityLog))
        {
            return string.Empty;
        }

        lock (_lock)
        {
            var lines = File.ReadAllLines(ConfigPaths.GuiAiActivityLog);
            return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
    }
}