namespace OllamaToolkit.Core.Modes;

public static class BenchmarkParallelSettings
{
    public const int MinParallel = 1;
    public const int MaxParallel = 4;

    public static int Clamp(int value) => Math.Clamp(value, MinParallel, MaxParallel);

    public static int GetParallel(
        ComputeMode mode,
        IReadOnlyDictionary<string, int>? byMode,
        int fallback = 1) =>
        GetParallel(mode.ToString(), byMode, fallback);

    public static int GetParallel(
        string modeName,
        IReadOnlyDictionary<string, int>? byMode,
        int fallback = 1)
    {
        if (byMode is not null
            && byMode.TryGetValue(modeName, out var value))
        {
            return Clamp(value);
        }

        return Clamp(fallback);
    }

    public static int GetParallelForLaunch(
        string? bestMode,
        IReadOnlyDictionary<string, int>? byMode,
        int fallback = 1)
    {
        if (string.IsNullOrWhiteSpace(bestMode)
            || !Enum.TryParse<ComputeMode>(bestMode, out var mode))
        {
            return Clamp(fallback);
        }

        return GetParallel(mode, byMode, fallback);
    }

    public static Dictionary<string, int> DefaultByMode(double sizeGb)
    {
        var size = sizeGb > 0 ? sizeGb : 4;
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CPU"] = 1,
            ["APU"] = size >= 8 ? 1 : 2,
            ["GPU"] = size >= 18 ? 1 : size >= 8 ? 2 : 3,
            ["Hybrid"] = size >= 18 ? 1 : 2,
            ["ROCm"] = size >= 18 ? 1 : size >= 8 ? 2 : 3
        };
    }

    public static Dictionary<string, int> EmbedDefaults() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CPU"] = 1,
            ["APU"] = 1,
            ["GPU"] = 1,
            ["Hybrid"] = 1,
            ["ROCm"] = 1
        };

    public static Dictionary<string, int> NormalizeByMode(
        IReadOnlyDictionary<string, int>? source,
        double sizeGb,
        bool isEmbed)
    {
        var defaults = isEmbed ? EmbedDefaults() : DefaultByMode(sizeGb);
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in Enum.GetValues<ComputeMode>())
        {
            var key = mode.ToString();
            if (source is not null && source.TryGetValue(key, out var value))
            {
                normalized[key] = Clamp(value);
            }
            else
            {
                normalized[key] = defaults[key];
            }
        }

        return normalized;
    }

    public static string FormatParallelSummary(IReadOnlyDictionary<string, int>? byMode)
    {
        if (byMode is null || byMode.Count == 0)
        {
            return "parallel: 1 (all modes)";
        }

        var parts = Enum.GetValues<ComputeMode>()
            .Select(m =>
            {
                var key = m.ToString();
                var value = byMode.TryGetValue(key, out var v) ? Clamp(v) : 1;
                return $"{key}={value}";
            });

        return "parallel: " + string.Join(", ", parts);
    }
}