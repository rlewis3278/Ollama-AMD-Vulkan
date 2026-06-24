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
            var sizes = new List<string>();
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
                    if (!sizes.Contains(tag))
                    {
                        sizes.Add(tag);
                    }
                }
                else if (!capabilities.Contains(tag))
                {
                    capabilities.Add(tag);
                }
            }

            results.Add(new LibraryCatalogEntry
            {
                Name = name,
                Description = description,
                Tags = string.Join(' ', capabilities),
                ParameterSize = FormatParameterSizeLabel(sizes),
                FileSize = "-"
            });
        }

        return results;
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
}