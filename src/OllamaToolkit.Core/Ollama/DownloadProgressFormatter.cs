namespace OllamaToolkit.Core.Ollama;

public static class DownloadProgressFormatter
{
    public static string FormatSpeedEta(ModelPullProgress update)
    {
        var parts = new List<string>();
        if (update.BytesPerSecond is > 0)
        {
            parts.Add($"{FormatMegabytesPerSecond(update.BytesPerSecond.Value)} MB/s");
        }

        if (update.EstimatedTimeRemaining is { } eta && eta > TimeSpan.Zero)
        {
            parts.Add($"ETA {FormatEta(eta)}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }

    public static string FormatMegabytesPerSecond(double bytesPerSecond) =>
        (bytesPerSecond / 1_048_576.0).ToString("0.0");

    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours}h {eta.Minutes}m";
        }

        if (eta.TotalMinutes >= 1)
        {
            return $"{(int)eta.TotalMinutes}m {eta.Seconds}s";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))}s";
    }
}