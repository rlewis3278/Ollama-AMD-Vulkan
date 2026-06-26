using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.ModelCategory;

namespace OllamaToolkit.BenchmarkRunner;

public sealed class AutomatedBenchmarkService
{
    private const string DefaultPrompt =
        "Summarize in one paragraph how local LLM GPU backend selection affects inference speed on Windows.";

    private const string DefaultEmbedInput =
        "Local LLM inference on Windows laptops benefits from choosing the right GPU backend. "
        + "Vulkan provides broad AMD compatibility while ROCm can accelerate integrated graphics. "
        + "Embedding models convert text into dense vectors for semantic search, retrieval augmented generation, "
        + "and clustering applications across document collections.";

    private readonly ModeService _modeService;
    private readonly OllamaApiClient _apiClient;
    private readonly ProfileStoreService _profiles;
    private readonly OllamaModelSessionService _modelSessions;
    private ComputeMode? _lastBenchmarkMode;

    public AutomatedBenchmarkService(
        ModeService? modeService = null,
        OllamaApiClient? apiClient = null,
        ProfileStoreService? profiles = null,
        OllamaModelSessionService? modelSessions = null)
    {
        _modeService = modeService ?? new ModeService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _profiles = profiles ?? new ProfileStoreService(_apiClient);
        _modelSessions = modelSessions ?? new OllamaModelSessionService();
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
        string? category = null,
        IProgress<string>? log = null,
        IProgress<BenchmarkProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        modes ??= [ComputeMode.CPU, ComputeMode.APU, ComputeMode.GPU, ComputeMode.Hybrid, ComputeMode.ROCm];
        outputDir ??= ProfileStoreService.NewGuiTestOutputDir(modelName);
        Directory.CreateDirectory(outputDir);
        _lastBenchmarkMode = null;

        var isEmbed = CategoryNormalizer.IsEmbeddingModel(modelName, category);
        var benchmarkKind = isEmbed ? BenchmarkKinds.Embed : BenchmarkKinds.Generate;

        var header = isEmbed
            ? BenchmarkProgressFormatter.EmbedModelHeader(
                modelName, modelIndex, modelCount, modes, aiSummarizerModel)
            : BenchmarkProgressFormatter.ModelHeader(
                modelName, modelIndex, modelCount, numCtx, numPredict, modes, aiSummarizerModel);

        BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
        {
            Phase = BenchmarkProgressPhase.ModelStarted,
            BenchmarkKind = benchmarkKind,
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
            BenchmarkKind = benchmarkKind,
            EmbedInput = isEmbed ? DefaultEmbedInput : null,
            Model = modelName,
            NumPredict = isEmbed ? 0 : numPredict,
            Runs = runs,
            OutputDir = outputDir,
            StartedAt = DateTimeOffset.Now.ToString("o"),
            Results = new List<BenchmarkReportModeResult>()
        };

        for (var modeIndex = 0; modeIndex < modes.Count; modeIndex++)
        {
            var mode = modes[modeIndex];
            cancellationToken.ThrowIfCancellationRequested();

            var modeTemplate = () => new BenchmarkProgressUpdate
            {
                Phase = BenchmarkProgressPhase.ModeApplying,
                BenchmarkKind = benchmarkKind,
                Model = modelName,
                ModelIndex = modelIndex,
                ModelCount = modelCount,
                Mode = mode.ToString(),
                ModeIndex = modeIndex,
                ModeCount = modes.Count,
                NumCtx = numCtx,
                NumPredict = numPredict,
                AiSummarizerModel = aiSummarizerModel
            };

            BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
            {
                ModeFraction = 0.05,
                ModeStatusDetail = "Applying mode…",
                LogLine = BenchmarkProgressFormatter.ModeApplying(modeIndex, modes.Count, mode)
            });

            var modeResult = new BenchmarkReportModeResult { Mode = mode.ToString() };
            var started = DateTime.UtcNow;
            string? modeEnvSummary = null;

            try
            {
                using var animator = new BenchmarkProgressAnimator(progress, modeTemplate);
                if (_lastBenchmarkMode != mode)
                {
                    animator.StartCreep(0.05, 0.18, "Applying mode & restarting Ollama…");
                    try
                    {
                        await _modelSessions.StopModelAsync(modelName, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Model may not be loaded yet.
                    }

                    await _modeService.ApplyModeWithRestartAsync(mode, saveBackup: false, cancellationToken)
                        .ConfigureAwait(false);
                    _apiClient.InvalidateCaches();
                    _lastBenchmarkMode = mode;
                    animator.StopCreep();
                    animator.Report(0.20, "Mode applied — loading model…");

                    modeEnvSummary = _modeService.GetManagedEnvSummary();
                    BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
                    {
                        Phase = BenchmarkProgressPhase.ModeApplying,
                        ModeFraction = 0.20,
                        ModeStatusDetail = "Mode applied — loading model…",
                        LogLine = BenchmarkProgressFormatter.ModeAppliedWithRestart(mode, modeEnvSummary)
                    });

                    animator.StartCreep(0.22, 0.32, "Warming model…");
                    await _modelSessions.SwitchToModelAsync(modelName, warmLoad: true, cancellationToken)
                        .ConfigureAwait(false);
                    animator.StopCreep();
                    animator.Report(0.35, "Model ready");
                }

                BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
                {
                    Phase = BenchmarkProgressPhase.ModeBenchmarking,
                    ModeFraction = 0.38,
                    ModeStatusDetail = isEmbed ? "Embedding…" : "Starting benchmark…",
                    LogLine = isEmbed
                        ? BenchmarkProgressFormatter.ModeEmbedBenchmarking(modeIndex, modes.Count, mode)
                        : BenchmarkProgressFormatter.ModeBenchmarking(modeIndex, modes.Count, mode)
                });

                if (isEmbed)
                {
                    animator.StartCreep(0.40, 0.92, "Running embed benchmark…");
                    var bench = await _apiClient.BenchmarkEmbedAsync(
                        modelName, DefaultEmbedInput, warmup: true, cancellationToken)
                        .ConfigureAwait(false);
                    animator.StopCreep();

                    modeResult.Status = "Success";
                    modeResult.EmbedLatencyMs = bench.LatencyMs;
                    modeResult.PromptEvalTps = bench.PromptEvalTps;
                    modeResult.Notes = BuildModeNotes(
                        $"prompt_tokens={bench.PromptEvalCount}; dims={bench.Dimensions}; embed_ms={bench.LatencyMs:F1}",
                        modeEnvSummary);
                    modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                    BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
                    {
                        Phase = BenchmarkProgressPhase.ModeCompleted,
                        ModeFraction = 1.0,
                        ModeStatusDetail = $"{bench.LatencyMs:F1} ms",
                        EmbedLatencyMs = bench.LatencyMs,
                        DurationSec = modeResult.DurationSec,
                        LogLine = BenchmarkProgressFormatter.ModeEmbedCompleted(
                            modeIndex, modes.Count, mode, bench.LatencyMs, modeResult.DurationSec)
                    });
                }
                else
                {
                    var tokenProgress = new Progress<int>(count =>
                    {
                        var target = numPredict > 0 ? numPredict : 32;
                        var fraction = 0.40 + 0.55 * Math.Clamp(count / (double)target, 0, 1);
                        animator.Report(
                            fraction,
                            $"Benchmarking… {Math.Min(count, target)}/{target} tokens");
                    });

                    var bench = await _apiClient.BenchmarkGenerateAsync(
                        modelName,
                        DefaultPrompt,
                        numPredict,
                        numCtx,
                        warmup: true,
                        tokenProgress,
                        cancellationToken)
                        .ConfigureAwait(false);

                    modeResult.Status = "Success";
                    modeResult.GenerationTps = bench.GenerationTps;
                    modeResult.PromptEvalTps = bench.PromptEvalTps;
                    modeResult.TtftMs = bench.TtftMs;
                    modeResult.Notes = BuildModeNotes(
                        $"gen_tokens={bench.EvalCount}; num_ctx={numCtx}",
                        modeEnvSummary);
                    modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                    BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
                    {
                        Phase = BenchmarkProgressPhase.ModeCompleted,
                        ModeFraction = 1.0,
                        ModeStatusDetail = $"{bench.GenerationTps:F1} tok/s",
                        GenerationTps = bench.GenerationTps,
                        DurationSec = modeResult.DurationSec,
                        LogLine = BenchmarkProgressFormatter.ModeCompleted(
                            modeIndex, modes.Count, mode, bench.GenerationTps, modeResult.DurationSec)
                    });
                }
            }
            catch (Exception ex)
            {
                modeResult.Status = "Failed";
                modeResult.Error = ex.Message;
                modeResult.Notes = isEmbed
                    ? $"Embed benchmark failed for mode {mode}."
                    : $"Benchmark failed for mode {mode}.";
                modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
                {
                    Phase = BenchmarkProgressPhase.ModeFailed,
                    ModeFraction = 1.0,
                    ModeStatusDetail = "Failed",
                    DurationSec = modeResult.DurationSec,
                    Error = ex.Message,
                    LogLine = BenchmarkProgressFormatter.ModeFailed(modeIndex, modes.Count, mode, ex.Message)
                });
            }

            report.Results!.Add(modeResult);
        }

        try
        {
            await _modelSessions.StopModelAsync(modelName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort unload after benchmark completes.
        }

        var successes = report.Results
            .Where(r => r.Status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .ToList();

        BenchmarkReportModeResult? winner = isEmbed
            ? successes.OrderBy(r => r.EmbedLatencyMs).FirstOrDefault()
            : successes.OrderByDescending(r => r.GenerationTps).FirstOrDefault();

        if (winner is not null)
        {
            report.Winner = new BenchmarkReportWinner
            {
                Mode = winner.Mode,
                GenerationTps = winner.GenerationTps,
                TtftMs = winner.TtftMs,
                EmbedLatencyMs = winner.EmbedLatencyMs,
                VramMb = winner.VramMb
            };
            report.Quantization = "unknown";

            BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
            {
                Phase = BenchmarkProgressPhase.ModelCompleted,
                BenchmarkKind = benchmarkKind,
                Model = modelName,
                ModelIndex = modelIndex,
                ModelCount = modelCount,
                ModeCount = modes.Count,
                BestMode = winner.Mode,
                BestTps = winner.GenerationTps,
                BestEmbedMs = winner.EmbedLatencyMs,
                LogLine = isEmbed
                    ? BenchmarkProgressFormatter.EmbedModelWinner(winner.Mode!, winner.EmbedLatencyMs)
                    : BenchmarkProgressFormatter.ModelWinner(winner.Mode!, winner.GenerationTps)
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
            BenchmarkKind = benchmarkKind,
            Model = modelName,
            ModelIndex = modelIndex,
            ModelCount = modelCount,
            ModeCount = modes.Count,
            LogLine = BenchmarkProgressFormatter.ReportSaved(reportPath)
        });

        return report;
    }

    private static string BuildModeNotes(string metrics, string? envSummary) =>
        string.IsNullOrWhiteSpace(envSummary) ? metrics : $"{metrics}; env={envSummary}";
}