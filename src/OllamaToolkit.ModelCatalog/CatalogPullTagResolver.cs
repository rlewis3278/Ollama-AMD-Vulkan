using System.Text.RegularExpressions;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static partial class CatalogPullTagResolver
{
    public static CatalogPullResolution Resolve(string libraryName, IReadOnlyList<LibraryTagInfo> tags)
    {
        if (tags.Count == 0)
        {
            return new CatalogPullResolution
            {
                PullTag = $"{libraryName}:latest",
                Resolved = false,
                IsCloudOnly = false
            };
        }

        var localTags = tags
            .Where(t => !t.IsCloud && t.FileSizeBytes is > 0)
            .ToList();
        var cloudTags = tags.Where(t => t.IsCloud).ToList();

        LibraryTagInfo chosen;
        var isCloudOnly = false;

        var latest = localTags.FirstOrDefault(t =>
            t.PullTag.EndsWith(":latest", StringComparison.OrdinalIgnoreCase));
        if (latest is not null)
        {
            chosen = latest;
        }
        else if (localTags.Count > 0)
        {
            chosen = localTags.OrderBy(t => t.FileSizeBytes).First();
        }
        else if (cloudTags.Count > 0)
        {
            chosen = cloudTags[0];
            isCloudOnly = cloudTags.Count == tags.Count;
        }
        else
        {
            chosen = tags[0];
            isCloudOnly = chosen.IsCloud;
        }

        return new CatalogPullResolution
        {
            PullTag = chosen.PullTag,
            Resolved = true,
            IsCloudOnly = isCloudOnly,
            ParameterSize = BuildParameterSizeLabel(tags),
            FileSize = BuildFileSizeLabel(localTags)
        };
    }

    public static CatalogPullResolution ResolveFromEntry(LibraryCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.DefaultPullTag))
        {
            return new CatalogPullResolution
            {
                PullTag = entry.DefaultPullTag,
                Resolved = true,
                IsCloudOnly = entry.IsCloudOnly,
                ParameterSize = entry.ParameterSize,
                FileSize = entry.FileSize
            };
        }

        return new CatalogPullResolution
        {
            PullTag = $"{entry.Name}:latest",
            Resolved = false,
            IsCloudOnly = entry.IsCloudOnly,
            ParameterSize = entry.ParameterSize,
            FileSize = entry.FileSize
        };
    }

    private static string BuildParameterSizeLabel(IReadOnlyList<LibraryTagInfo> tags)
    {
        var suffixes = new List<string>();
        foreach (var tag in tags)
        {
            var suffix = tag.PullTag.Split(':', 2).ElementAtOrDefault(1);
            if (string.IsNullOrWhiteSpace(suffix)
                || suffix.Equals("latest", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("cloud", StringComparison.OrdinalIgnoreCase)
                || suffix.Contains("instruct", StringComparison.OrdinalIgnoreCase)
                || suffix.Contains("text-", StringComparison.OrdinalIgnoreCase)
                || suffix.Contains('_'))
            {
                continue;
            }

            if (ParamSuffixToken().IsMatch(suffix))
            {
                suffixes.Add(suffix.ToUpperInvariant());
            }
        }

        if (suffixes.Count == 0)
        {
            return "-";
        }

        return string.Join(' ', suffixes.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildFileSizeLabel(IReadOnlyList<LibraryTagInfo> localTags)
    {
        if (localTags.Count == 0)
        {
            return "-";
        }

        var bytes = localTags
            .Where(t => t.FileSizeBytes is > 0)
            .Select(t => t.FileSizeBytes!.Value)
            .ToList();
        if (bytes.Count == 0)
        {
            return "-";
        }

        var min = bytes.Min();
        var max = bytes.Max();
        if (min == max)
        {
            return FormatBytes(min);
        }

        return $"{FormatBytes(min)}-{FormatBytes(max)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        return $"{bytes / (1024.0 * 1024):F0} MB";
    }

    [GeneratedRegex(@"^(?:\d+(?:\.\d+)?[bkm]?|\d+x\d+b)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ParamSuffixToken();
}