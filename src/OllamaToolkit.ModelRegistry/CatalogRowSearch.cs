using OllamaToolkit.ModelRegistry.Models;

namespace OllamaToolkit.ModelRegistry;

public static class CatalogRowSearch
{
    public static bool Matches(CatalogRowViewModel row, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return true;
        }

        var searchable = BuildSearchableFields(row);
        return tokens.All(token =>
            searchable.Any(field => field.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<string> Tokenize(string query) =>
        query.Trim()
            .Split([' ', ':', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToList();

    private static List<string> BuildSearchableFields(CatalogRowViewModel row)
    {
        var fields = new List<string>
        {
            row.Name,
            row.LibraryName,
            row.DefaultPullTag,
            row.Category,
            row.Description,
            row.DownloadDescription,
            row.AiDescription,
            row.DisplayDescription,
            row.Tags,
            row.SizeUsage,
            row.ContextDisplay,
            row.InputModalities,
            row.ParameterSize,
            row.BestMode,
            row.BestTps
        };

        var colon = row.Name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && colon < row.Name.Length - 1)
        {
            fields.Add(row.Name[(colon + 1)..]);
        }

        return fields.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
    }
}