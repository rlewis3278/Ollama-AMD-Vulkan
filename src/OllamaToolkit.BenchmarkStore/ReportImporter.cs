namespace OllamaToolkit.BenchmarkStore;

public sealed class ReportImporter
{
    private readonly ProfileStoreService _profiles;

    public ReportImporter(ProfileStoreService? profiles = null)
    {
        _profiles = profiles ?? new ProfileStoreService();
    }

    public async Task<ImportReportsResult> ImportReportsAsync(
        string? reportsRoot = null,
        CancellationToken cancellationToken = default)
    {
        reportsRoot ??= Core.ToolkitPaths.ReportsRoot;
        if (!Directory.Exists(reportsRoot))
        {
            return new ImportReportsResult();
        }

        var latestByModel = new Dictionary<string, (string Path, string CompletedAt)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(reportsRoot, "report.json", SearchOption.AllDirectories))
        {
            try
            {
                var profile = await ReportParser.ParseReportAsync(file, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (profile?.LastTested is null)
                {
                    continue;
                }

                var report = await Core.JsonFileHelper
                    .ReadAsync<Models.BenchmarkReportDocument>(file, cancellationToken)
                    .ConfigureAwait(false);
                if (report?.Model is null)
                {
                    continue;
                }

                if (!latestByModel.TryGetValue(report.Model, out var existing)
                    || string.Compare(profile.LastTested, existing.CompletedAt, StringComparison.Ordinal) > 0)
                {
                    latestByModel[report.Model] = (file, profile.LastTested);
                }
            }
            catch
            {
                // Skip corrupt reports.
            }
        }

        var importedNames = new List<string>();
        var store = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (modelName, entry) in latestByModel)
        {
            var profile = await ReportParser.ParseReportAsync(entry.Path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                continue;
            }

            var report = await Core.JsonFileHelper
                .ReadAsync<Models.BenchmarkReportDocument>(entry.Path, cancellationToken)
                .ConfigureAwait(false);
            if (report?.Model is null)
            {
                continue;
            }

            store.Models[report.Model] = profile;
            importedNames.Add(modelName);
        }

        if (importedNames.Count > 0)
        {
            await _profiles.SaveAsync(store, cancellationToken).ConfigureAwait(false);
        }

        return new ImportReportsResult
        {
            ModelNames = importedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }
}