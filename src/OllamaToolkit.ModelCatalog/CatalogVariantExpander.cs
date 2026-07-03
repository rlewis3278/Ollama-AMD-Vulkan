using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static class CatalogVariantExpander
{
    public static IReadOnlyList<LibraryCatalogEntry> Expand(
        LibraryCatalogEntry template,
        IReadOnlyList<LibraryTagInfo> tags)
    {
        var libraryName = CatalogEntryNames.LibraryName(template);
        if (tags.Count == 0)
        {
            template.LibraryName = libraryName;
            if (string.IsNullOrWhiteSpace(template.DefaultPullTag))
            {
                template.DefaultPullTag = CatalogEntryNames.PullTag(template);
            }

            return [template];
        }

        var results = new List<LibraryCatalogEntry>(tags.Count);
        foreach (var tag in tags)
        {
            results.Add(CreateVariantEntry(template, libraryName, tag));
        }

        return results;
    }

    public static void PreserveVariantMetadata(LibraryCatalogEntry target, LibraryCatalogEntry previous)
    {
        if (previous.FileSizeConfirmed)
        {
            target.FileSizeBytes = previous.FileSizeBytes;
            target.FileSizeConfirmed = true;
            target.FileSizeConfirmedAt = previous.FileSizeConfirmedAt;
            target.FileSize = previous.FileSize;
        }
        else if (!string.IsNullOrWhiteSpace(previous.EstimatedFileSize) && previous.EstimatedFileSize != "-")
        {
            target.EstimatedFileSize = previous.EstimatedFileSize;
            if (target.FileSize is "-" or "")
            {
                target.FileSize = previous.EstimatedFileSize;
            }
        }

        if (!string.IsNullOrWhiteSpace(previous.Category))
        {
            target.Category = previous.Category;
        }

        if (previous.SortOrder != 0)
        {
            target.SortOrder = previous.SortOrder;
        }

        if (!string.IsNullOrWhiteSpace(previous.ListDescription))
        {
            target.ListDescription = previous.ListDescription;
        }
    }

    private static LibraryCatalogEntry CreateVariantEntry(
        LibraryCatalogEntry template,
        string libraryName,
        LibraryTagInfo tag)
    {
        var suffix = tag.PullTag.Split(':', 2).ElementAtOrDefault(1) ?? "latest";
        var sizeUsage = tag.SizeUsage ?? tag.FileSizeLabel ?? (tag.IsCloud ? "Cloud" : "-");
        var fileSize = tag.FileSizeLabel ?? sizeUsage;

        return new LibraryCatalogEntry
        {
            Name = tag.PullTag,
            LibraryName = libraryName,
            Description = template.Description,
            Tags = template.Tags,
            ParameterSize = FormatParameterSuffix(suffix),
            SizeUsage = sizeUsage,
            ContextWindow = tag.ContextWindow ?? "-",
            InputModalities = tag.InputModalities ?? "-",
            FileSize = fileSize,
            EstimatedFileSize = fileSize,
            FileSizeBytes = tag.FileSizeBytes ?? 0,
            DefaultPullTag = tag.PullTag,
            IsCloudOnly = tag.IsCloud,
            Category = template.Category,
            SortOrder = template.SortOrder,
            ListDescription = template.ListDescription
        };
    }

    private static string FormatParameterSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)
            || suffix.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || suffix.Equals("cloud", StringComparison.OrdinalIgnoreCase))
        {
            return "-";
        }

        if (suffix.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix[..^6];
        }

        return suffix.ToUpperInvariant();
    }
}