namespace OllamaToolkit.Core.Settings;

public sealed class AiSettingsDocument
{
    public string? PreferredSummarizerModel { get; set; }
    public bool ToolkitAiEnabled { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool BlockProgramAiDuringTests { get; set; } = true;
    public bool RestartOllamaForAiOperations { get; set; } = false;
    public int ConcurrentAiWorkers { get; set; } = 3;
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