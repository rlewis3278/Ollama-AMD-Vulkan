using System.Globalization;
using System.Text.RegularExpressions;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static partial class OllamaLibraryTagsParser
{
    private const int TagRowScanLength = 2800;

    public static IReadOnlyList<LibraryTagInfo> ParseTagsHtml(string html, string libraryName)
    {
        var results = new List<LibraryTagInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in TagRowHiddenInput().Matches(html))
        {
            if (!TryCreateTagInfo(match, libraryName, seen, out var tag))
            {
                continue;
            }

            results.Add(tag);
        }

        if (results.Count == 0)
        {
            foreach (Match match in HiddenCommandInput().Matches(html))
            {
                if (!TryCreateTagInfoFromHiddenInput(html, match, libraryName, seen, out var tag))
                {
                    continue;
                }

                results.Add(tag);
            }
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
                    IsCloud = IsCloudPullTag(pullTag)
                });
            }
        }

        return results;
    }

    private static bool TryCreateTagInfo(
        Match match,
        string libraryName,
        HashSet<string> seen,
        out LibraryTagInfo tag)
    {
        tag = null!;
        var pullTag = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(pullTag)
            || !pullTag.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase)
            || !seen.Add(pullTag))
        {
            return false;
        }

        string? sizeLabel = null;
        if (match.Groups[2].Success && match.Groups[3].Success)
        {
            sizeLabel = $"{match.Groups[2].Value.Trim()}{match.Groups[3].Value.ToUpperInvariant()}";
        }

        tag = new LibraryTagInfo
        {
            PullTag = pullTag,
            FileSizeLabel = sizeLabel,
            FileSizeBytes = TryParseSizeBytes(sizeLabel),
            IsCloud = IsCloudPullTag(pullTag, sizeLabel)
        };
        return true;
    }

    private static bool TryCreateTagInfoFromHiddenInput(
        string html,
        Match match,
        string libraryName,
        HashSet<string> seen,
        out LibraryTagInfo tag)
    {
        tag = null!;
        var pullTag = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(pullTag)
            || !pullTag.StartsWith($"{libraryName}:", StringComparison.OrdinalIgnoreCase)
            || !seen.Add(pullTag))
        {
            return false;
        }

        var start = match.Index + match.Length;
        var length = Math.Min(TagRowScanLength, html.Length - start);
        var window = length > 0 ? html[start..(start + length)] : string.Empty;
        var sizeLabel = TryParseNearbySizeLabel(window);

        tag = new LibraryTagInfo
        {
            PullTag = pullTag,
            FileSizeLabel = sizeLabel,
            FileSizeBytes = TryParseSizeBytes(sizeLabel),
            IsCloud = IsCloudPullTag(pullTag, sizeLabel)
        };
        return true;
    }

    private static bool IsCloudPullTag(string pullTag, string? sizeLabel = null) =>
        pullTag.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
        || pullTag.Contains("-cloud", StringComparison.OrdinalIgnoreCase)
        || (sizeLabel is null && pullTag.Contains("cloud", StringComparison.OrdinalIgnoreCase));

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

    [GeneratedRegex(
        @"<input class=""command hidden"" value=""([^""]+)""[^>]*/>[\s\S]{0,2800}?<p class=""col-span-2[^""]*"">\s*(\d+(?:\.\d+)?)\s*(GB|MB|KB)\s*</p>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TagRowHiddenInput();

    [GeneratedRegex(@"<input class=""command hidden"" value=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HiddenCommandInput();

    [GeneratedRegex(@"href=""/library/[^""]+:([^""/?#]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LibraryTagHref();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(GB|MB|KB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NearbySizeLabel();

    [GeneratedRegex(@"\b(\d+(?:\.\d+)?[BKM]?)\s+(?:total\s+)?parameters\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DescriptionParamToken();
}