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

        var (sizeUsage, contextWindow, inputModalities) = ParseRowMetadataColumns(match.Value);
        tag = new LibraryTagInfo
        {
            PullTag = pullTag,
            FileSizeLabel = sizeLabel ?? sizeUsage,
            FileSizeBytes = TryParseSizeBytes(sizeLabel ?? sizeUsage),
            SizeUsage = sizeUsage,
            ContextWindow = contextWindow,
            InputModalities = inputModalities,
            IsCloud = IsCloudPullTag(pullTag, sizeLabel ?? sizeUsage)
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
        var (sizeUsage, contextWindow, inputModalities) = ParseRowMetadataColumns(window);

        tag = new LibraryTagInfo
        {
            PullTag = pullTag,
            FileSizeLabel = sizeLabel ?? sizeUsage,
            FileSizeBytes = TryParseSizeBytes(sizeLabel ?? sizeUsage),
            SizeUsage = sizeUsage ?? sizeLabel,
            ContextWindow = contextWindow,
            InputModalities = inputModalities,
            IsCloud = IsCloudPullTag(pullTag, sizeLabel ?? sizeUsage)
        };
        return true;
    }

    private static (string? SizeUsage, string? ContextWindow, string? InputModalities) ParseRowMetadataColumns(
        string window)
    {
        var columnValues = new List<string>();
        foreach (Match match in MetadataColumnParagraph().Matches(window))
        {
            var value = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                columnValues.Add(value);
            }
        }

        if (MetadataColumnDiv().Match(window) is { Success: true } divMatch)
        {
            var inputValue = divMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inputValue))
            {
                columnValues.Add(inputValue);
            }
        }

        if (columnValues.Count == 0)
        {
            return (null, null, null);
        }

        var sizeUsage = columnValues[0];
        var context = columnValues.Count > 1 ? NormalizeContextLabel(columnValues[1]) : null;
        var inputModalities = columnValues.Count > 2 ? columnValues[2] : null;
        return (sizeUsage, context, inputModalities);
    }

    private static string NormalizeContextLabel(string value) =>
        value.Replace(" context window", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

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

    [GeneratedRegex(@"<p class=""col-span-2[^""]*"">\s*([^<]+?)\s*</p>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetadataColumnParagraph();

    [GeneratedRegex(@"<div class=""col-span-2[^""]*""[^>]*>\s*([^<]+?)\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetadataColumnDiv();
}