using System.Text;
using System.Text.RegularExpressions;

namespace OllamaToolkit.Core.Ollama;

/// <summary>
/// Strips terminal noise from Ollama CLI output and compresses errors for logs and UI.
/// </summary>
public static class OllamaOutputSanitizer
{
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled);

    private static readonly string[] MeaningfulLineMarkers =
    [
        "error:",
        "failed",
        "ggml_",
        "llama_",
        "vulkan",
        "outofmemory",
        "out of memory",
        "internal server error",
        "timed out",
        "connection",
        "refused",
        "500 ",
        "cuda",
        "hip",
    ];

    public static string StripAnsi(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = AnsiEscapeRegex.Replace(text, string.Empty);
        stripped = stripped.Replace("\r", string.Empty);
        return stripped.Trim();
    }

    public static string SanitizeCliOutput(string? text, int maxLength = 480)
    {
        var cleaned = StripAnsi(text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "(no output)";
        }

        var summary = ExtractMeaningfulSummary(cleaned);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = CollapseWhitespace(cleaned);
        }

        return Truncate(summary, maxLength);
    }

    public static string FormatException(Exception ex, int maxLength = 480)
    {
        if (ex is OperationCanceledException)
        {
            return "Cancelled.";
        }

        var message = StripAnsi(ex.Message);
        if (ex.InnerException is not null)
        {
            var inner = StripAnsi(ex.InnerException.Message);
            if (!string.IsNullOrWhiteSpace(inner) && !message.Contains(inner, StringComparison.OrdinalIgnoreCase))
            {
                message = $"{message} ({inner})";
            }
        }

        message = SanitizeCliOutput(message, maxLength);
        return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message;
    }

    private static string ExtractMeaningfulSummary(string cleaned)
    {
        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meaningful = new List<string>();

        foreach (var line in lines)
        {
            if (line.Length < 4)
            {
                continue;
            }

            if (LooksLikeSpinnerNoise(line))
            {
                continue;
            }

            var lower = line.ToLowerInvariant();
            if (MeaningfulLineMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal)))
            {
                meaningful.Add(line);
            }
        }

        if (meaningful.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(" | ", meaningful.Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
    }

    private static bool LooksLikeSpinnerNoise(string line) =>
        line.All(ch => ch is '⠋' or '⠙' or '⠹' or '⠸' or '⠼' or '⠴' or '⠦' or '⠧' or '⠇' or '⠏'
            or '[' or ']' or 'G' or 'K' or '?' or ' ' or 'h' or 'l' or '2' or '5');

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 3)] + "...";
    }
}