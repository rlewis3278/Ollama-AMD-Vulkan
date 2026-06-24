using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.BenchmarkRunner;

public sealed class AutomatedBenchmarkService
{
    private const string DefaultPrompt =
        "Summarize in one paragraph how AMD Vulkan GPU selection affects local LLM inference speed on Windows 11.";

    private readonly ModeService _modeService;
    private readonly OllamaApiClient _apiClient;
    private readonly ProfileStoreService _profiles;

    public AutomatedBenchmarkService(
        ModeService? modeService = null,
        OllamaApiClient? apiClient = null,
        ProfileStoreService? profiles = null)
    {
        _modeService = modeService ?? new ModeService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _profiles = profiles ?? new ProfileStoreService(_apiClient);
    }

    public async Task<BenchmarkReportDocument> RunAsync(
        string modelName,
        IReadOnlyList<ComputeMode>? modes = null,
        int numPredict = 32,
        int numCtx = 8192,
        int runs = 1,
        string? outputDir = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        modes ??= [ComputeMode.CPU, ComputeMode.APU, ComputeMode.GPU, ComputeMode.Hybrid];
        outputDir ??= ProfileStoreService.NewGuiTestOutputDir(modelName);
        Directory.CreateDirectory(outputDir);

        var report = new BenchmarkReportDocument
        {
            Model = modelName,
            NumPredict = numPredict,
            Runs = runs,
            OutputDir = outputDir,
            StartedAt = DateTimeOffset.Now.ToString("o"),
            Results = new List<BenchmarkReportModeResult>()
        };

        foreach (var mode in modes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Report($"Testing mode {mode}...");

            var modeResult = new BenchmarkReportModeResult { Mode = mode.ToString() };
            var started = DateTime.UtcNow;

            try
            {
                await _modeService.ApplyModeAsync(mode, restartOllama: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var bench = await _apiClient.BenchmarkGenerateAsync(
                    modelName, DefaultPrompt, numPredict, numCtx, warmup: true, cancellationToken)
                    .ConfigureAwait(false);

                modeResult.Status = "Success";
                modeResult.GenerationTps = bench.GenerationTps;
                modeResult.PromptEvalTps = bench.PromptEvalTps;
                modeResult.TtftMs = bench.TtftMs;
                modeResult.Notes = $"gen_tokens={bench.EvalCount}; num_ctx={numCtx}";
                log?.Report($"  {mode}: {bench.GenerationTps:F2} tok/s");
            }
            catch (Exception ex)
            {
                modeResult.Status = "Failed";
                modeResult.Error = ex.Message;
                modeResult.Notes = $"Benchmark failed for mode {mode}.";
                log?.Report($"  {mode}: FAIL - {ex.Message}");
            }

            modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
            report.Results!.Add(modeResult);
        }

        var winner = report.Results
            .Where(r => r.Status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.GenerationTps)
            .FirstOrDefault();

        if (winner is not null)
        {
            report.Winner = new BenchmarkReportWinner
            {
                Mode = winner.Mode,
                GenerationTps = winner.GenerationTps,
                TtftMs = winner.TtftMs,
                VramMb = winner.VramMb
            };
            report.Quantization = "unknown";
        }

        report.CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var reportPath = Path.Combine(outputDir, "report.json");
        await JsonFileHelper.WriteAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
        await _profiles.UpdateFromReportAsync(reportPath, numCtx, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        log?.Report($"Report saved: {reportPath}");
        return report;
    }
}