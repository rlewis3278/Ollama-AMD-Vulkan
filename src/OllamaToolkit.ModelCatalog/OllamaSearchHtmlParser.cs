using System.Net;
using System.Text.RegularExpressions;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static partial class OllamaSearchHtmlParser
{
    private const string SearchUrl = "https://ollama.com/search";

    public static string BuildSearchUrl(string query) =>
        $"{SearchUrl}?q={Uri.EscapeDataString(query.Trim())}";

    public static IReadOnlyList<LibraryCatalogEntry> ParseSearchHtml(string html)
    {
        var results = new List<LibraryCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ModelHref().Matches(html))
        {
            var path = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(path)
                || path.StartsWith("library/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("search", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = path.Replace("/", ":");
            if (!name.Contains(':') || !seen.Add(name))
            {
                continue;
            }

            var blockStart = Math.Max(0, match.Index - 64);
            var blockEnd = Math.Min(html.Length, match.Index + 2048);
            var block = html[blockStart..blockEnd];

            var description = string.Empty;
            if (DescriptionParagraph().Match(block) is { Success: true } descMatch)
            {
                description = WebUtility.HtmlDecode(descMatch.Groups[1].Value).Trim();
            }

            var capabilities = new List<string>();
            foreach (Match tm in TagSpan().Matches(block))
            {
                var tag = tm.Groups[1].Value.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(tag) && !capabilities.Contains(tag))
                {
                    capabilities.Add(tag);
                }
            }

            var parameterSize = OllamaLibraryTagsParser.TryParseParamsFromDescription(description);
            var isCloudOnly = capabilities.Contains("cloud", StringComparer.OrdinalIgnoreCase);

            results.Add(new LibraryCatalogEntry
            {
                Name = name,
                LibraryName = name.Split(':')[0],
                DefaultPullTag = name,
                Description = description,
                Tags = string.Join(' ', capabilities),
                ParameterSize = parameterSize,
                FileSize = "-",
                IsCloudOnly = isCloudOnly
            });
        }

        return results;
    }

    [GeneratedRegex(@"href=""/([^""/?#]+/[^""/?#]+)""", RegexOptions.Compiled)]
    private static partial Regex ModelHref();

    [GeneratedRegex(@"<p class=""max-w-lg break-words text-neutral-800 text-md"">([^<]*)</p>", RegexOptions.Compiled)]
    private static partial Regex DescriptionParagraph();

    [GeneratedRegex(@"x-test-capability\s+class=""[^""]*"">([^<]+)</span>", RegexOptions.Compiled)]
    private static partial Regex TagSpan();
}