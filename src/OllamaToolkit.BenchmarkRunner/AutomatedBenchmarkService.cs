using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.BenchmarkRunner;

public sealed class AutomatedBenchmarkService
{
    private const string DefaultPrompt =
        "Summarize in one paragraph how local LLM GPU backend selection affects inference speed on Windows.";

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
        int modelIndex = 0,
        int modelCount = 1,
        string? aiSummarizerModel = null,
        IProgress<string>? log = null,
        IProgress<BenchmarkProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        modes ??= [ComputeMode.CPU, ComputeMode.APU, ComputeMode.GPU, ComputeMode.Hybrid, ComputeMode.ROCm];
        outputDir ??= ProfileStoreService.NewGuiTestOutputDir(modelName);
        Directory.CreateDirectory(outputDir);

        var header = BenchmarkProgressFormatter.ModelHeader(
            modelName, modelIndex, modelCount, numCtx, numPredict, modes, aiSummarizerModel);
        BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
        {
            Phase = BenchmarkProgressPhase.ModelStarted,
            Model = modelName,
            ModelIndex = modelIndex,
            ModelCount = modelCount,
            ModeCount = modes.Count,
            NumCtx = numCtx,
            NumPredict = numPredict,
            AiSummarizerModel = aiSummarizerModel,
            LogLine = header
        });

        var report = new BenchmarkReportDocument
        {
            Model = modelName,
            NumPredict = numPredict,
            Runs = runs,
            OutputDir = outputDir,
            StartedAt = DateTimeOffset.Now.ToString("o"),
            Results = new List<BenchmarkReportModeResult>()
        };

        for (var modeIndex = 0; modeIndex < modes.Count; modeIndex++)
        {
            var mode = modes[modeIndex];
            cancellationToken.ThrowIfCancellationRequested();

            BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
            {
                Phase = BenchmarkProgressPhase.ModeApplying,
                Model = modelName,
                ModelIndex = modelIndex,
                ModelCount = modelCount,
                Mode = mode.ToString(),
                ModeIndex = modeIndex,
                ModeCount = modes.Count,
                NumCtx = numCtx,
                NumPredict = numPredict,
                LogLine = BenchmarkProgressFormatter.ModeApplying(modeIndex, modes.Count, mode)
            });

            var modeResult = new BenchmarkReportModeResult { Mode = mode.ToString() };
            var started = DateTime.UtcNow;

            try
            {
                await _modeService.ApplyModeAsync(mode, restartOllama: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
                {
                    Phase = BenchmarkProgressPhase.ModeBenchmarking,
                    Model = modelName,
                    ModelIndex = modelIndex,
                    ModelCount = modelCount,
                    Mode = mode.ToString(),
                    ModeIndex = modeIndex,
                    ModeCount = modes.Count,
                    NumCtx = numCtx,
                    NumPredict = numPredict,
                    LogLine = BenchmarkProgressFormatter.ModeBenchmarking(modeIndex, modes.Count, mode)
                });

                var bench = await _apiClient.BenchmarkGenerateAsync(
                    modelName, DefaultPrompt, numPredict, numCtx, warmup: true, cancellationToken)
                    .ConfigureAwait(false);

                modeResult.Status = "Success";
                modeResult.GenerationTps = bench.GenerationTps;
                modeResult.PromptEvalTps = bench.PromptEvalTps;
                modeResult.TtftMs = bench.TtftMs;
                modeResult.Notes = $"gen_tokens={bench.EvalCount}; num_ctx={numCtx}";
                modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
                {
                    Phase = BenchmarkProgressPhase.ModeCompleted,
                    Model = modelName,
                    ModelIndex = modelIndex,
                    ModelCount = modelCount,
                    Mode = mode.ToString(),
                    ModeIndex = modeIndex,
                    ModeCount = modes.Count,
                    NumCtx = numCtx,
                    NumPredict = numPredict,
                    GenerationTps = bench.GenerationTps,
                    DurationSec = modeResult.DurationSec,
                    LogLine = BenchmarkProgressFormatter.ModeCompleted(
                        modeIndex, modes.Count, mode, bench.GenerationTps, modeResult.DurationSec)
                });
            }
            catch (Exception ex)
            {
                modeResult.Status = "Failed";
                modeResult.Error = ex.Message;
                modeResult.Notes = $"Benchmark failed for mode {mode}.";
                modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
                {
                    Phase = BenchmarkProgressPhase.ModeFailed,
                    Model = modelName,
                    ModelIndex = modelIndex,
                    ModelCount = modelCount,
                    Mode = mode.ToString(),
                    ModeIndex = modeIndex,
                    ModeCount = modes.Count,
                    NumCtx = numCtx,
                    NumPredict = numPredict,
                    DurationSec = modeResult.DurationSec,
                    Error = ex.Message,
                    LogLine = BenchmarkProgressFormatter.ModeFailed(modeIndex, modes.Count, mode, ex.Message)
                });
            }

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

            BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
            {
                Phase = BenchmarkProgressPhase.ModelCompleted,
                Model = modelName,
                ModelIndex = modelIndex,
                ModelCount = modelCount,
                ModeCount = modes.Count,
                BestMode = winner.Mode,
                BestTps = winner.GenerationTps,
                LogLine = BenchmarkProgressFormatter.ModelWinner(winner.Mode!, winner.GenerationTps)
            });
        }

        report.CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var reportPath = Path.Combine(outputDir, "report.json");
        await JsonFileHelper.WriteAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
        await _profiles.UpdateFromReportAsync(reportPath, numCtx, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
        {
            Phase = BenchmarkProgressPhase.ModelCompleted,
            Model = modelName,
            ModelIndex = modelIndex,
            ModelCount = modelCount,
            ModeCount = modes.Count,
            LogLine = BenchmarkProgressFormatter.ReportSaved(reportPath)
        });

        return report;
    }
}