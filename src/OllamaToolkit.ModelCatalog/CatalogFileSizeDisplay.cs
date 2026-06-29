using OllamaToolkit.Core;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static class CatalogFileSizeDisplay
{
    public static string GetDisplayLabel(LibraryCatalogEntry entry, bool markEstimate = true)
    {
        if (entry.IsCloudOnly)
        {
            return "Cloud";
        }

        if (entry.FileSizeConfirmed && entry.FileSizeBytes > 0)
        {
            return ModelSizeFormatter.FormatBytes(entry.FileSizeBytes);
        }

        if (!string.IsNullOrWhiteSpace(entry.FileSize) && entry.FileSize != "-")
        {
            return markEstimate && !entry.FileSizeConfirmed
                ? $"~{entry.FileSize.Trim()}"
                : entry.FileSize.Trim();
        }

        if (!string.IsNullOrWhiteSpace(entry.EstimatedFileSize) && entry.EstimatedFileSize != "-")
        {
            return markEstimate ? $"~{entry.EstimatedFileSize.Trim()}" : entry.EstimatedFileSize.Trim();
        }

        return "-";
    }
}