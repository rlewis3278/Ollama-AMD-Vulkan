using OllamaToolkit.BenchmarkStore.Models;

namespace OllamaToolkit.BenchmarkStore;

public static class ReportProfileValidator
{
    public static IReadOnlyList<string> FindMismatches(
        BenchmarkReportDocument report,
        ModelProfileEntry profile)
    {
        var issues = new List<string>();
        if (report.Results is null || profile.Results is null)
        {
            return issues;
        }

        foreach (var row in report.Results)
        {
            if (!ProfileSanitizer.IsSupportedMode(row.Mode))
            {
                continue;
            }

            if (!profile.Results.TryGetValue(row.Mode!, out var saved))
            {
                issues.Add($"{row.Mode}: missing in profile");
                continue;
            }

            var reportSuccess = IsSuccess(row.Status);
            var profileSuccess = IsSuccess(saved.Status);
            if (reportSuccess != profileSuccess)
            {
                issues.Add(
                    $"{row.Mode}: report={row.Status ?? "?"} profile={saved.Status ?? "?"}");
            }
        }

        if (report.Winner?.Mode is { } winnerMode
            && ProfileSanitizer.IsSupportedMode(winnerMode)
            && !string.Equals(profile.BestMode, winnerMode, StringComparison.OrdinalIgnoreCase)
            && profile.Results.Values.Any(r => IsSuccess(r.Status)))
        {
            issues.Add($"bestMode: report={winnerMode} profile={profile.BestMode ?? "(empty)"}");
        }

        return issues;
    }

    private static bool IsSuccess(string? status) =>
        status?.Equals("Success", StringComparison.OrdinalIgnoreCase) == true;
}