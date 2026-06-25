namespace OllamaToolkit.Core;

public static class ModelSizeFormatter
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "-";
        }

        const double gb = 1_073_741_824.0;
        const double mb = 1_048_576.0;
        const double kb = 1024.0;

        if (bytes >= gb)
        {
            return $"{bytes / gb:F2} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:F1} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:F0} KB";
        }

        return $"{bytes} B";
    }

    public static string FormatGb(double sizeGb) =>
        sizeGb > 0 ? $"{sizeGb:F2} GB" : "-";

    public static string FormatSizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label == "-")
        {
            return "-";
        }

        return label.Trim();
    }

    public static bool TryParseSizeLabelToBytes(string? label, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(label) || label == "-")
        {
            return false;
        }

        var text = label.Trim();
        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            text = text[..dash].Trim();
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], out var value))
        {
            return false;
        }

        var unit = parts[1].ToUpperInvariant();
        bytes = unit switch
        {
            "GB" => (long)(value * 1_073_741_824.0),
            "MB" => (long)(value * 1_048_576.0),
            "KB" => (long)(value * 1024.0),
            "B" => (long)value,
            _ => 0
        };

        return bytes > 0;
    }
}