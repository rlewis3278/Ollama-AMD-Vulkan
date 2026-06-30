namespace OllamaToolkit.ModelCategory;

public static class CategorySubcategoryCatalog
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByMajorCategory =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["General Chat"] = ["Assistant", "Roleplay", "Instruction", "Multilingual", "Uncensored"],
            ["Coding"] = ["General Code", "PowerShell", "Python", "Web/JS", "Systems/Rust", "SQL/Data"],
            ["Reasoning"] = ["Math", "Logic", "Chain-of-Thought", "STEM", "Exam/Tutor"],
            ["Vision"] = ["OCR", "Image Caption", "Document", "UI/Screenshot"],
            ["Multimodal"] = ["Audio", "Video", "Speech", "Cross-Modal"],
            ["Embedding"] = ["Text", "Code", "Multilingual", "Retrieval/RAG"],
            ["Tools"] = ["Function Calling", "Agents", "Automation", "MCP/Plugins"],
            ["Domain Specific"] = ["Medical", "Legal", "Finance", "Science", "Security"],
            ["Other"] = ["Experimental", "Merged/MoE", "Custom Fine-Tune", "Unknown"]
        };

    public static string NormalizeSubcategory(string majorCategory, string? subcategory)
    {
        if (string.IsNullOrWhiteSpace(subcategory))
        {
            return DefaultFor(majorCategory);
        }

        var trimmed = subcategory.Trim().Trim('"', '\'', '.');
        if (!ByMajorCategory.TryGetValue(majorCategory, out var allowed))
        {
            return trimmed;
        }

        foreach (var option in allowed)
        {
            if (trimmed.Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        foreach (var option in allowed)
        {
            if (trimmed.Contains(option, StringComparison.OrdinalIgnoreCase)
                || option.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return trimmed.Length > 48 ? trimmed[..48] : trimmed;
    }

    public static string DefaultFor(string majorCategory) =>
        ByMajorCategory.TryGetValue(majorCategory, out var list) && list.Count > 0
            ? list[0]
            : "Unknown";

    public static string HeuristicSubcategory(string majorCategory, string name, string description, string tags)
    {
        var text = $"{name} {description} {tags}".ToLowerInvariant();
        return majorCategory switch
        {
            "Coding" when text.Contains("powershell") => "PowerShell",
            "Coding" when text.Contains("python") => "Python",
            "Coding" when text.Contains("rust") => "Systems/Rust",
            "Coding" when text.Contains("sql") || text.Contains("data") => "SQL/Data",
            "Coding" when text.Contains("javascript") || text.Contains("typescript") => "Web/JS",
            "Reasoning" when text.Contains("math") => "Math",
            "Reasoning" when text.Contains("think") => "Chain-of-Thought",
            "Vision" when text.Contains("ocr") => "OCR",
            "Embedding" when text.Contains("code") => "Code",
            "Embedding" when text.Contains("multilingual") => "Multilingual",
            "Tools" when text.Contains("agent") => "Agents",
            "Tools" when text.Contains("mcp") => "MCP/Plugins",
            "Domain Specific" when text.Contains("med") => "Medical",
            "Domain Specific" when text.Contains("legal") => "Legal",
            "Domain Specific" when text.Contains("financ") => "Finance",
            _ => DefaultFor(majorCategory)
        };
    }

    public static string FormatListForPrompt() =>
        string.Join(Environment.NewLine, ByMajorCategory.Select(kv =>
            $"- {kv.Key}: {string.Join(", ", kv.Value)}"));
}