namespace OllamaToolkit.ModelCategory;

public static class CategoryNormalizer
{
    public static readonly IReadOnlyList<string> AllCategories =
    [
        "General Chat", "Coding", "Reasoning", "Vision", "Multimodal",
        "Embedding", "Tools", "Domain Specific", "Other"
    ];

    public static readonly IReadOnlyDictionary<string, int> SortOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["General Chat"] = 1,
        ["Coding"] = 2,
        ["Reasoning"] = 3,
        ["Vision"] = 4,
        ["Multimodal"] = 5,
        ["Embedding"] = 6,
        ["Tools"] = 7,
        ["Domain Specific"] = 8,
        ["Other"] = 9
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chat"] = "General Chat",
        ["general"] = "General Chat",
        ["assistant"] = "General Chat",
        ["code"] = "Coding",
        ["programming"] = "Coding",
        ["developer"] = "Coding",
        ["reasoning"] = "Reasoning",
        ["math"] = "Reasoning",
        ["thinking"] = "Reasoning",
        ["image"] = "Vision",
        ["visual"] = "Vision",
        ["ocr"] = "Vision",
        ["multimodal"] = "Multimodal",
        ["audio"] = "Multimodal",
        ["embed"] = "Embedding",
        ["embeddings"] = "Embedding",
        ["retrieval"] = "Embedding",
        ["tool use"] = "Tools",
        ["function calling"] = "Tools",
        ["agent"] = "Tools",
        ["domain"] = "Domain Specific",
        ["medical"] = "Domain Specific",
        ["legal"] = "Domain Specific",
        ["finance"] = "Domain Specific"
    };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Other";
        }

        var trimmed = text.Trim().Trim('"', '\'', '.');
        foreach (var cat in AllCategories)
        {
            if (trimmed.Equals(cat, StringComparison.OrdinalIgnoreCase))
            {
                return cat;
            }
        }

        var lower = trimmed.ToLowerInvariant();
        foreach (var alias in Aliases)
        {
            if (lower.Contains(alias.Key, StringComparison.Ordinal))
            {
                return alias.Value;
            }
        }

        return "Other";
    }

    public static string HeuristicCategory(string name, string description, string tags)
    {
        var text = $"{name} {description} {tags}".ToLowerInvariant();
        if (text.Contains("embed", StringComparison.Ordinal))
        {
            return "Embedding";
        }

        if (text.Contains("vision", StringComparison.Ordinal)
            || text.Contains("llava", StringComparison.Ordinal)
            || text.Contains("moondream", StringComparison.Ordinal))
        {
            return "Vision";
        }

        if (text.Contains("coder", StringComparison.Ordinal)
            || text.Contains("code", StringComparison.Ordinal)
            || text.Contains("starcoder", StringComparison.Ordinal))
        {
            return "Coding";
        }

        if (text.Contains("reason", StringComparison.Ordinal)
            || text.Contains("math", StringComparison.Ordinal))
        {
            return "Reasoning";
        }

        if (text.Contains("tool", StringComparison.Ordinal)
            || text.Contains("agent", StringComparison.Ordinal))
        {
            return "Tools";
        }

        return "General Chat";
    }

    public static int GetSortOrder(string category)
    {
        var major = ExtractMajorCategory(category);
        return SortOrder.TryGetValue(major, out var order) ? order : 9;
    }

    public static string ExtractMajorCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "Other";
        }

        var trimmed = category.Trim();
        var sep = trimmed.IndexOf('·');
        if (sep < 0)
        {
            sep = trimmed.IndexOf('>');
        }

        var major = sep > 0 ? trimmed[..sep].Trim() : trimmed;
        return Normalize(major);
    }

    public static string FormatDisplay(string majorCategory, string? subcategory = null)
    {
        var major = Normalize(majorCategory);
        var sub = string.IsNullOrWhiteSpace(subcategory)
            ? CategorySubcategoryCatalog.DefaultFor(major)
            : CategorySubcategoryCatalog.NormalizeSubcategory(major, subcategory);
        return $"{major} · {sub}";
    }

    public static bool IsEmbeddingModel(string modelName, string? category = null)
    {
        if (!string.IsNullOrWhiteSpace(category)
            && category.Trim().Equals("Embedding", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var baseName = modelName.Split(':')[0].ToLowerInvariant();
        return baseName.Contains("embed", StringComparison.Ordinal)
            || baseName.Contains("minilm", StringComparison.Ordinal)
            || baseName.Contains("arctic-embed", StringComparison.Ordinal)
            || baseName.Contains("nomic-embed", StringComparison.Ordinal)
            || baseName.Contains("mxbai-embed", StringComparison.Ordinal)
            || baseName.Contains("bge-", StringComparison.Ordinal)
            || baseName.Contains("e5-", StringComparison.Ordinal)
            || baseName.Contains("gte-", StringComparison.Ordinal);
    }
}