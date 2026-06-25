using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;

namespace OllamaToolkit.BenchmarkStore;

public static class ReportParser
{
    public static async Task<ModelProfileEntry?> ParseReportAsync(
        string reportPath,
        int numCtx = 8192,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reportPath))
        {
            return null;
        }

        var report = await JsonFileHelper.ReadAsync<BenchmarkReportDocument>(reportPath, cancellationToken)
            .ConfigureAwait(false);
        if (report?.Model is null)
        {
            return null;
        }

        var isEmbed = report.BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase);
        var results = new Dictionary<string, ModeResultEntry>(StringComparer.OrdinalIgnoreCase);
        if (report.Results is not null)
        {
            foreach (var row in report.Results)
            {
                results[row.Mode] = new ModeResultEntry
                {
                    Status = row.Status,
                    GenerationTps = row.GenerationTps,
                    PromptEvalTps = row.PromptEvalTps,
                    TtftMs = row.TtftMs,
                    EmbedLatencyMs = row.EmbedLatencyMs,
                    VramMb = row.VramMb,
                    Notes = row.Notes,
                    Error = row.Error
                };
            }
        }

        return new ModelProfileEntry
        {
            BenchmarkKind = isEmbed ? BenchmarkKinds.Embed : BenchmarkKinds.Generate,
            BestMode = report.Winner?.Mode,
            BestTps = report.Winner?.GenerationTps ?? 0,
            BestEmbedMs = report.Winner?.EmbedLatencyMs ?? 0,
            Quantization = report.Quantization ?? "unknown",
            NumCtx = isEmbed ? 0 : numCtx,
            NumPredict = isEmbed ? 0 : report.NumPredict > 0 ? report.NumPredict : 32,
            Runs = report.Runs > 0 ? report.Runs : 1,
            LastTested = report.CompletedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Results = results,
            ReportPath = Path.GetFullPath(reportPath),
            OutputDir = report.OutputDir ?? Path.GetDirectoryName(reportPath)
        };
    }
}