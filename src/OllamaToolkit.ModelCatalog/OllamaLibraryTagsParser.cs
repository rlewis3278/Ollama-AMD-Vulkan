using System.Globalization;
using System.Text.RegularExpressions;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static partial class OllamaLibraryTagsParser
{
    public static IReadOnlyList<LibraryTagInfo> ParseTagsHtml(string html, string libraryName)
    {
        var results = new List<LibraryTagInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HiddenCommandInput().Matches(html))
        {
            var pullTag = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(pullTag)
                || !pullTag.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(pullTag))
            {
                continue;
            }

            var window = html.Substring(
                match.Index,
                Math.Min(1200, html.Length - match.Index));
            var sizeLabel = TryParseNearbySizeLabel(window);
            var isCloud = pullTag.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
                || (sizeLabel is null && window.Contains("cloud", StringComparison.OrdinalIgnoreCase));

            results.Add(new LibraryTagInfo
            {
                PullTag = pullTag,
                FileSizeLabel = sizeLabel,
                FileSizeBytes = TryParseSizeBytes(sizeLabel),
                IsCloud = isCloud
            });
        }

        if (results.Count == 0)
        {
            foreach (Match match in LibraryTagHref().Matches(html))
            {
                var pullTag = $"{libraryName}:{match.Groups[1].Value.Trim()}";
                if (!seen.Add(pullTag))
                {
                    continue;
                }

                results.Add(new LibraryTagInfo
                {
                    PullTag = pullTag,
                    IsCloud = pullTag.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return results;
    }

    public static string TryParseParamsFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "-";
        }

        var matches = DescriptionParamToken().Matches(description);
        if (matches.Count == 0)
        {
            return "-";
        }

        var tokens = matches
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tokens.Count == 0 ? "-" : string.Join(' ', tokens);
    }

    private static string? TryParseNearbySizeLabel(string window)
    {
        foreach (Match match in NearbySizeLabel().Matches(window))
        {
            var label = $"{match.Groups[1].Value.Trim()}{match.Groups[2].Value.ToUpperInvariant()}";
            if (TryParseSizeBytes(label) is > 0)
            {
                return label;
            }
        }

        return null;
    }

    private static long? TryParseSizeBytes(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var normalized = label.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var numberPart = new string(normalized.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (normalized.EndsWith("GB", StringComparison.Ordinal))
        {
            return (long)(value * 1024 * 1024 * 1024);
        }

        if (normalized.EndsWith("MB", StringComparison.Ordinal))
        {
            return (long)(value * 1024 * 1024);
        }

        if (normalized.EndsWith("KB", StringComparison.Ordinal))
        {
            return (long)(value * 1024);
        }

        return null;
    }

    [GeneratedRegex(@"<input class=""command hidden"" value=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HiddenCommandInput();

    [GeneratedRegex(@"href=""/library/[^""]+:([^""/?#]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LibraryTagHref();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(GB|MB|KB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NearbySizeLabel();

    [GeneratedRegex(@"\b(\d+(?:\.\d+)?[BKM]?)\s+(?:total\s+)?parameters\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DescriptionParamToken();
}