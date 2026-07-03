using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public static class CatalogEntryNames
{
    public static string LibraryName(LibraryCatalogEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.LibraryName)
            ? entry.LibraryName
            : entry.Name.Split(':')[0];

    public static string PullTag(LibraryCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.DefaultPullTag))
        {
            return entry.DefaultPullTag;
        }

        return entry.Name.Contains(':', StringComparison.Ordinal)
            ? entry.Name
            : $"{entry.Name}:latest";
    }

    public static bool IsVariantRow(LibraryCatalogEntry entry) =>
        entry.Name.Contains(':', StringComparison.Ordinal);

    public static bool CanExpandFromOfficialLibrary(string libraryName) =>
        !string.IsNullOrWhiteSpace(libraryName)
        && !libraryName.Contains('/')
        && !libraryName.Contains(':', StringComparison.Ordinal);
}