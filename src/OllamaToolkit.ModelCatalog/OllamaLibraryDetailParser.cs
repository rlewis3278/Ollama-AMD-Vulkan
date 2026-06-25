using System.Globalization;
using System.Text.RegularExpressions;

namespace OllamaToolkit.ModelCatalog;

public static partial class OllamaLibraryDetailParser
{
    public static string ParseFileSizeRange(string html)
    {
        var sizesGb = new List<double>();
        foreach (Match match in ModelTagSize().Matches(html))
        {
            var label = match.Groups[1].Value.Trim();
            if (TryParseSizeGb(label, out var gb))
            {
                sizesGb.Add(gb);
            }
        }

        if (sizesGb.Count == 0)
        {
            return "-";
        }

        var min = sizesGb.Min();
        var max = sizesGb.Max();
        if (Math.Abs(min - max) < 0.05)
        {
            return FormatGb(min);
        }

        return $"{FormatGb(min)}-{FormatGb(max)}";
    }

    private static bool TryParseSizeGb(string label, out double gb)
    {
        gb = 0;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var normalized = label.Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        var numberPart = new string(normalized.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (normalized.EndsWith("GB", StringComparison.Ordinal))
        {
            gb = value;
            return true;
        }

        if (normalized.EndsWith("MB", StringComparison.Ordinal))
        {
            gb = value / 1024.0;
            return true;
        }

        if (normalized.EndsWith("KB", StringComparison.Ordinal))
        {
            gb = value / (1024.0 * 1024.0);
            return true;
        }

        return false;
    }

    private static string FormatGb(double gb) =>
        gb >= 1 ? $"{gb:F1} GB" : $"{gb * 1024:F0} MB";

    [GeneratedRegex(@"x-test-model-tag-size[^>]*>\s*([^<]+)\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ModelTagSize();
}