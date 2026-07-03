using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static class CatalogMetadataLookup
{
    public static IReadOnlyDictionary<string, LibraryCatalogEntry> BuildPullTagIndex(
        IReadOnlyList<LibraryCatalogEntry> entries)
    {
        var map = new Dictionary<string, LibraryCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            map[entry.Name] = entry;
            var pullTag = CatalogEntryNames.PullTag(entry);
            if (!string.IsNullOrWhiteSpace(pullTag))
            {
                map[pullTag] = entry;
            }

            var libraryName = CatalogEntryNames.LibraryName(entry);
            if (!map.ContainsKey(libraryName))
            {
                map[libraryName] = entry;
            }
        }

        return map;
    }

    public static bool TryGetForModel(
        IReadOnlyDictionary<string, LibraryCatalogEntry> index,
        string modelOrTag,
        out LibraryCatalogEntry entry)
    {
        if (index.TryGetValue(modelOrTag, out entry!))
        {
            return true;
        }

        var library = modelOrTag.Split(':')[0];
        return index.TryGetValue(library, out entry!);
    }

    public static string ResolveSizeUsage(LibraryCatalogEntry? entry, string? installedFileSize = null)
    {
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.SizeUsage) && entry.SizeUsage != "-")
        {
            return entry.SizeUsage;
        }

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.FileSize) && entry.FileSize != "-")
        {
            return entry.FileSize;
        }

        return string.IsNullOrWhiteSpace(installedFileSize) ? "-" : installedFileSize;
    }

    public static string ResolveContext(LibraryCatalogEntry? entry, int benchmarkCtx = 0)
    {
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.ContextWindow) && entry.ContextWindow != "-")
        {
            return entry.ContextWindow;
        }

        return benchmarkCtx > 0 ? benchmarkCtx.ToString() : "-";
    }

    public static int ResolveContextSortKey(LibraryCatalogEntry? entry, int benchmarkCtx = 0)
    {
        if (benchmarkCtx > 0)
        {
            return benchmarkCtx;
        }

        return ParseContextSortKey(entry?.ContextWindow);
    }

    public static int ParseContextSortKey(string? contextDisplay)
    {
        if (string.IsNullOrWhiteSpace(contextDisplay) || contextDisplay == "-")
        {
            return 0;
        }

        var normalized = contextDisplay.Trim().ToUpperInvariant();
        var numberPart = new string(normalized.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        if (normalized.EndsWith("K", StringComparison.Ordinal))
        {
            return (int)(value * 1000);
        }

        if (normalized.EndsWith("M", StringComparison.Ordinal))
        {
            return (int)(value * 1_000_000);
        }

        return (int)value;
    }

    public static long ResolveFileSizeSortKey(LibraryCatalogEntry? entry, string? displayLabel = null)
    {
        if (entry is { FileSizeBytes: > 0 })
        {
            return entry.FileSizeBytes;
        }

        if (!string.IsNullOrWhiteSpace(displayLabel) && displayLabel != "-")
        {
            return CatalogFileSizeResolver.GetSortBytesFromDisplayLabel(displayLabel);
        }

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.SizeUsage) && entry.SizeUsage != "-")
        {
            return CatalogFileSizeResolver.GetSortBytesFromDisplayLabel(entry.SizeUsage);
        }

        return long.MaxValue;
    }

    public static long ResolveMetricSortKey(string benchmarkKind, double bestTps, double bestEmbedMs)
    {
        if (benchmarkKind.Equals("Embed", StringComparison.OrdinalIgnoreCase))
        {
            return bestEmbedMs > 0 ? (long)Math.Round(bestEmbedMs * 100) : 0;
        }

        return bestTps > 0 ? (long)Math.Round(bestTps * 100) : 0;
    }

    public static string ResolveInput(LibraryCatalogEntry? entry)
    {
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.InputModalities) && entry.InputModalities != "-")
        {
            return entry.InputModalities;
        }

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Tags))
        {
            return InferInputFromTags(entry.Tags);
        }

        return "-";
    }

    private static string InferInputFromTags(string tags)
    {
        var parts = new List<string> { "Text" };
        if (tags.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("multimodal", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("Image");
        }

        if (tags.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("Audio");
        }

        return string.Join(", ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}