using System.Net;
using System.Text.RegularExpressions;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static partial class OllamaLibraryHtmlParser
{
    public static IReadOnlyList<LibraryCatalogEntry> ParseListingHtml(string html)
    {
        var results = new List<LibraryCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = LibraryBlockSplit().Split(html);

        foreach (var block in blocks)
        {
            if (!block.Contains("x-test-model", StringComparison.Ordinal))
            {
                continue;
            }

            var nameMatch = LibraryHref().Match(block);
            if (!nameMatch.Success)
            {
                continue;
            }

            var name = nameMatch.Groups[1].Value.Trim();
            if (name.Contains(':') || !seen.Add(name))
            {
                continue;
            }

            var description = string.Empty;
            var descMatch = DescriptionParagraph().Match(block);
            if (descMatch.Success)
            {
                description = WebUtility.HtmlDecode(descMatch.Groups[1].Value).Trim();
            }

            var capabilities = new List<string>();
            var paramSizes = new List<string>();
            var fileSizeLabels = new List<string>();
            foreach (Match tm in TagSpan().Matches(block))
            {
                var kind = tm.Groups[1].Value;
                var tag = tm.Groups[2].Value.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                if (kind.Equals("size", StringComparison.OrdinalIgnoreCase))
                {
                    if (FileSizeToken().IsMatch(tag))
                    {
                        if (!fileSizeLabels.Contains(tag))
                        {
                            fileSizeLabels.Add(tag);
                        }
                    }
                    else if (!paramSizes.Contains(tag))
                    {
                        paramSizes.Add(tag);
                    }
                }
                else if (!capabilities.Contains(tag))
                {
                    capabilities.Add(tag);
                }
            }

            var fileSize = FormatFileSizeLabels(fileSizeLabels);
            if (fileSize == "-" && FileSizeInBlock().Match(block) is { Success: true } blockMatch)
            {
                fileSize = blockMatch.Groups[1].Value.Trim().ToUpperInvariant();
            }

            results.Add(new LibraryCatalogEntry
            {
                Name = name,
                Description = description,
                Tags = string.Join(' ', capabilities),
                ParameterSize = FormatParameterSizeLabel(paramSizes),
                FileSize = fileSize
            });
        }

        return results;
    }

    private static string FormatFileSizeLabels(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return "-";
        }

        var normalized = labels
            .Select(l => l.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant())
            .Distinct()
            .ToList();
        return normalized.Count == 1 ? normalized[0] : string.Join("-", normalized);
    }

    private static string FormatParameterSizeLabel(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return "-";
        }

        if (tokens.Count == 1)
        {
            return tokens[0].ToUpperInvariant();
        }

        return string.Join(' ', tokens.Select(t => t.ToUpperInvariant()));
    }

    [GeneratedRegex(@"(?=<li x-test-model)", RegexOptions.Compiled)]
    private static partial Regex LibraryBlockSplit();

    [GeneratedRegex(@"href=""/library/([^""/?#]+)""", RegexOptions.Compiled)]
    private static partial Regex LibraryHref();

    [GeneratedRegex(@"<p class=""max-w-lg break-words text-neutral-800 text-md"">([^<]*)</p>", RegexOptions.Compiled)]
    private static partial Regex DescriptionParagraph();

    [GeneratedRegex(@"x-test-(capability|size)\s+class=""[^""]*"">([^<]+)</span>", RegexOptions.Compiled)]
    private static partial Regex TagSpan();

    [GeneratedRegex(@"^[\d.]+\s*(?:gb|mb|kb)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FileSizeToken();

    [GeneratedRegex(@"([\d.]+\s*(?:GB|MB))(?:\s*</|\s+\d+K|\s+&bull;|\s+context)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FileSizeInBlock();
}