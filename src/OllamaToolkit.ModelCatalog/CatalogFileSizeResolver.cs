using OllamaToolkit.Core;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static class CatalogFileSizeResolver
{
    public static long GetSortBytes(LibraryCatalogEntry entry)
    {
        if (entry.FileSizeConfirmed && entry.FileSizeBytes > 0)
        {
            return entry.FileSizeBytes;
        }

        if (TryParseLabel(entry.EstimatedFileSize, out var estimated))
        {
            return estimated;
        }

        if (TryParseLabel(entry.FileSize, out var listed))
        {
            return listed;
        }

        return long.MaxValue;
    }

    public static (long Bytes, bool HasKnown) GetSortBytesWithKnown(LibraryCatalogEntry entry)
    {
        if (entry.FileSizeConfirmed && entry.FileSizeBytes > 0)
        {
            return (entry.FileSizeBytes, true);
        }

        var bytes = GetSortBytes(entry);
        return (bytes, bytes < long.MaxValue);
    }

    public static long GetSortBytesFromDisplayLabel(string? displayLabel)
    {
        if (TryParseLabel(displayLabel, out var bytes))
        {
            return bytes;
        }

        return long.MaxValue;
    }

    private static bool TryParseLabel(string? label, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(label) || label == "-")
        {
            return false;
        }

        var text = label.Trim();
        if (text.StartsWith('~'))
        {
            text = text[1..].Trim();
        }

        return ModelSizeFormatter.TryParseSizeLabelToBytes(text, out bytes);
    }
}