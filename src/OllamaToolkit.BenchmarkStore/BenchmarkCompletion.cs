using OllamaToolkit.BenchmarkStore.Models;

namespace OllamaToolkit.BenchmarkStore;

public static class BenchmarkCompletion
{
    public static int RequiredModeCount => ProfileSanitizer.SupportedModes.Length;

    public static bool HasAllModeResults(Dictionary<string, ModeResultEntry>? results) =>
        results is not null
        && ProfileSanitizer.SupportedModes.All(mode =>
            results.TryGetValue(mode, out var row) && !string.IsNullOrWhiteSpace(row.Status));

    public static bool HasAnySuccessfulMode(Dictionary<string, ModeResultEntry>? results) =>
        results?.Values.Any(IsSuccessfulStatus) == true;

    public static bool HasFailedModeResults(ModelProfileSummary summary) =>
        summary.Results?.Values.Any(IsFailedStatus) == true;

    public static bool HasFailedModeResults(ModelProfileEntry profile) =>
        profile.Results?.Values.Any(IsFailedStatus) == true;

    public static bool IsFullyTested(ModelProfileSummary summary) =>
        !summary.NeedsRetest
        && ProfileSanitizer.IsSupportedMode(summary.BestMode)
        && HasAllModeResults(summary.Results)
        && HasAnySuccessfulMode(summary.Results);

    public static bool IsFullyTested(ModelProfileEntry? profile) =>
        profile is not null
        && ProfileSanitizer.IsSupportedMode(profile.BestMode)
        && HasAllModeResults(profile.Results)
        && HasAnySuccessfulMode(profile.Results);

    public static bool IsAllModesFailed(Dictionary<string, ModeResultEntry>? results) =>
        HasAllModeResults(results) && !HasAnySuccessfulMode(results);

    public static string FormatModeStatusSummary(Dictionary<string, ModeResultEntry>? results)
    {
        if (results is null || results.Count == 0)
        {
            return "Untested";
        }

        if (IsAllModesFailed(results))
        {
            return "FAILED";
        }

        var success = ProfileSanitizer.SupportedModes.Count(mode =>
            results.TryGetValue(mode, out var row) && IsSuccessfulStatus(row));
        return $"{success}/{RequiredModeCount} OK";
    }

    public static bool ShouldRetest(ModelProfileSummary summary)
    {
        if (IsFullyTested(summary))
        {
            return false;
        }

        if (summary.NeedsRetest || HasFailedModeResults(summary))
        {
            return true;
        }

        var resultCount = summary.Results?.Count ?? 0;
        return resultCount > 0 && resultCount < RequiredModeCount;
    }

    public static bool ShouldRetest(ModelProfileEntry? profile)
    {
        if (IsFullyTested(profile))
        {
            return false;
        }

        if (profile is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(profile.BestMode) || !ProfileSanitizer.IsSupportedMode(profile.BestMode))
        {
            return true;
        }

        if (HasFailedModeResults(profile))
        {
            return true;
        }

        var resultCount = profile.Results?.Count ?? 0;
        return resultCount > 0 && resultCount < RequiredModeCount;
    }

    private static bool IsSuccessfulStatus(ModeResultEntry row) =>
        row.Status.Equals("Success", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedStatus(ModeResultEntry row) =>
        row.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || row.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase);
}