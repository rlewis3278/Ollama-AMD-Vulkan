namespace OllamaToolkit.Core.Settings;

public sealed class AiSettingsDocument
{
    public string? PreferredSummarizerModel { get; set; }
    public bool ToolkitAiEnabled { get; set; } = true;
    public Dictionary<string, bool> FeatureFlags { get; set; } = CreateDefaultFeatureFlags();

    public static Dictionary<string, bool> CreateDefaultFeatureFlags() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["DescriptionSummarization"] = true,
        ["CatalogCategorization"] = true,
        ["BenchmarkInterpreter"] = true,
        ["FailureDiagnosis"] = true,
        ["NaturalLanguageSearch"] = true,
        ["ModelPickerAdvisor"] = true,
        ["OptimalBenchmarkSettings"] = true,
        ["TestQueuePrioritization"] = true,
        ["ModelComparison"] = true,
        ["PlainLanguageErrors"] = true,
        ["LogAnomalyDetection"] = true
    };
}