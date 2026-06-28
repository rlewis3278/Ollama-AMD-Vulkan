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
        + "Vulkan provides broad AMD compatibility on Windows laptops with discrete and integrated GPUs. "
        + "Embedding models convert text into dense vectors for semantic search, retrieval augmented generation, "
        + "and clustering applications across document collections.";

    private readonly ModeService _modeService;
    private readonly OllamaServerTuningService _serverTuning;
    private readonly OllamaApiClient _apiClient;
    private readonly ProfileStoreService _profiles;
    private readonly OllamaModelSessionService _modelSessions;
    private ComputeMode? _lastBenchmarkMode;

    public AutomatedBenchmarkService(
        ModeService? modeService = null,
        OllamaServerTuningService? serverTuning = null,
        OllamaApiClient? apiClient = null,
        ProfileStoreService? profiles = null,
        OllamaModelSessionService? modelSessions = null)
    {
        _modeService = modeService ?? new ModeService();
        _serverTuning = serverTuning ?? new OllamaServerTuningService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _profiles = profiles ?? new ProfileStoreService(_apiClient);
        _modelSessions = modelSessions ?? new OllamaModelSessionService();
    }

    public async Task<BenchmarkReportDocument> RunAsync(
        string modelName,
        IReadOnlyList<ComputeMode>? modes = null,
        int numPredict = 32,
        int numCtx = 8192,
        IReadOnlyDictionary<string, int>? numParallelByMode = null,
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
        modes ??= [ComputeMode.CPU, ComputeMode.APU, ComputeMode.GPU, ComputeMode.Hybrid];
        outputDir ??= ProfileStoreService.NewGuiTestOutputDir(modelName);
        Directory.CreateDirectory(outputDir);
        _lastBenchmarkMode = null;

        var isEmbed = CategoryNormalizer.IsEmbeddingModel(modelName, category);
        var benchmarkKind = isEmbed ? BenchmarkKinds.Embed : BenchmarkKinds.Generate;

        var header = isEmbed
            ? BenchmarkProgressFormatter.EmbedModelHeader(
                modelName, modelIndex, modelCount, modes, aiSummarizerModel, numParallelByMode)
            : BenchmarkProgressFormatter.ModelHeader(
                modelName, modelIndex, modelCount, numCtx, numPredict, modes, aiSummarizerModel, numParallelByMode);

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
            NumCtx = isEmbed ? 0 : numCtx,
            NumParallelByMode = numParallelByMode is null
                ? null
                : new Dictionary<string, int>(numParallelByMode, StringComparer.OrdinalIgnoreCase),
            Runs = runs,
            OutputDir = outputDir,
            StartedAt = DateTimeOffset.Now.ToString("o"),
            Results = new List<BenchmarkReportModeResult>()
        };

        for (var modeIndex = 0; modeIndex < modes.Count; modeIndex++)
        {
            var mode = modes[modeIndex];
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureApiReadyForModeAsync(modelName, mode, modeIndex, modes.Count, log, cancellationToken)
                .ConfigureAwait(false);

            var modePhase = BenchmarkProgressPhase.ModeApplying;
            var modeTemplate = () => new BenchmarkProgressUpdate
            {
                Phase = modePhase,
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

            var modeResult = new BenchmarkReportModeResult { Mode = mode.ToString() };
            var modeParallel = BenchmarkParallelSettings.GetParallel(mode, numParallelByMode);
            var started = DateTime.UtcNow;
            string? modeEnvSummary = null;
            var lastFraction = 0.0;

            try
            {
                if (isEmbed)
                {
                    ReportEmbedMilestone(
                        progress, log, modeTemplate, ref modePhase,
                        EmbedModeMilestone.ModeStarted,
                        BenchmarkProgressFormatter.ModeApplying(modeIndex, modes.Count, mode, modeParallel));

                    if (_lastBenchmarkMode != mode)
                    {
                        ReportEmbedMilestone(progress, log, modeTemplate, ref modePhase, EmbedModeMilestone.ApplyingMode);
                        _serverTuning.ApplyParallelForMode(mode, numParallelByMode);
                        await _modeService.ApplyModeWithRestartAsync(mode, saveBackup: false, cancellationToken)
                            .ConfigureAwait(false);
                        _apiClient.InvalidateCaches();
                        _lastBenchmarkMode = mode;
                        modeEnvSummary = _modeService.GetManagedEnvSummary();

                        ReportEmbedMilestone(
                            progress, log, modeTemplate, ref modePhase,
                            EmbedModeMilestone.ModeApplied,
                            BenchmarkProgressFormatter.ModeAppliedWithRestart(mode, modeEnvSummary));

                        ReportEmbedMilestone(progress, log, modeTemplate, ref modePhase, EmbedModeMilestone.WarmingModel);
                        await _modelSessions.SwitchToModelAsync(modelName, warmLoad: true, cancellationToken)
                            .ConfigureAwait(false);
                        ReportEmbedMilestone(progress, log, modeTemplate, ref modePhase, EmbedModeMilestone.ModelReady);
                    }
                    else
                    {
                        ReportEmbedMilestone(progress, log, modeTemplate, ref modePhase, EmbedModeMilestone.ModelReady);
                    }

                    modePhase = BenchmarkProgressPhase.ModeBenchmarking;
                    ReportEmbedMilestone(
                        progress, log, modeTemplate, ref modePhase,
                        EmbedModeMilestone.EmbedWarmup,
                        BenchmarkProgressFormatter.ModeEmbedBenchmarking(modeIndex, modes.Count, mode));

                    var embedStage = new Progress<string>(stage =>
                    {
                        if (stage == "measure")
                        {
                            ReportEmbedMilestone(progress, log, modeTemplate, ref modePhase, EmbedModeMilestone.EmbedMeasure);
                        }
                    });

                    var bench = await _apiClient.BenchmarkEmbedAsync(
                        modelName, DefaultEmbedInput, warmup: true, embedStage, cancellationToken)
                        .ConfigureAwait(false);

                    modeResult.Status = "Success";
                    modeResult.EmbedLatencyMs = bench.LatencyMs;
                    modeResult.PromptEvalTps = bench.PromptEvalTps;
                    modeResult.Notes = BuildModeNotes(
                        $"prompt_tokens={bench.PromptEvalCount}; dims={bench.Dimensions}; embed_ms={bench.LatencyMs:F1}; num_parallel={modeParallel}",
                        modeEnvSummary);
                    modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                    modePhase = BenchmarkProgressPhase.ModeCompleted;
                    lastFraction = 1.0;
                    ReportEmbedMilestone(
                        progress, log, modeTemplate, ref modePhase,
                        EmbedModeMilestone.Complete,
                        BenchmarkProgressFormatter.ModeEmbedCompleted(
                            modeIndex, modes.Count, mode, bench.LatencyMs, modeResult.DurationSec),
                        $"{bench.LatencyMs:F1} ms",
                        bench.LatencyMs);
                }
                else
                {
                    ReportGenerateMilestone(
                        progress, log, modeTemplate, ref modePhase,
                        GenerateModeMilestone.ModeStarted,
                        BenchmarkProgressFormatter.ModeApplying(modeIndex, modes.Count, mode, modeParallel));

                    if (_lastBenchmarkMode != mode)
                    {
                        ReportGenerateMilestone(progress, log, modeTemplate, ref modePhase, GenerateModeMilestone.StoppingModel);
                        try
                        {
                            await _modelSessions.StopModelAsync(modelName, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Model may not be loaded yet.
                        }

                        ReportGenerateMilestone(progress, log, modeTemplate, ref modePhase, GenerateModeMilestone.ApplyingMode);
                        _serverTuning.ApplyParallelForMode(mode, numParallelByMode);
                        await _modeService.ApplyModeWithRestartAsync(mode, saveBackup: false, cancellationToken)
                            .ConfigureAwait(false);
                        _apiClient.InvalidateCaches();
                        _lastBenchmarkMode = mode;
                        modeEnvSummary = _modeService.GetManagedEnvSummary();

                        ReportGenerateMilestone(
                            progress, log, modeTemplate, ref modePhase,
                            GenerateModeMilestone.ModeApplied,
                            BenchmarkProgressFormatter.ModeAppliedWithRestart(mode, modeEnvSummary));

                        ReportGenerateMilestone(progress, log, modeTemplate, ref modePhase, GenerateModeMilestone.WarmingModel);
                        await _modelSessions.SwitchToModelAsync(modelName, warmLoad: true, cancellationToken)
                            .ConfigureAwait(false);
                        ReportGenerateMilestone(progress, log, modeTemplate, ref modePhase, GenerateModeMilestone.ModelReady);
                    }
                    else
                    {
                        ReportGenerateMilestone(progress, log, modeTemplate, ref modePhase, GenerateModeMilestone.ModelReady);
                    }

                    modePhase = BenchmarkProgressPhase.ModeBenchmarking;
                    ReportGenerateMilestone(
                        progress, log, modeTemplate, ref modePhase,
                        GenerateModeMilestone.WarmupStarted,
                        BenchmarkProgressFormatter.ModeBenchmarking(modeIndex, modes.Count, mode));

                    var warmupComplete = false;
                    var tokenProgress = new Progress<int>(count =>
                    {
                        if (!warmupComplete && count > 0)
                        {
                            warmupComplete = true;
                            lastFraction = BenchmarkModeMilestones.GenerateFraction(GenerateModeMilestone.WarmupComplete);
                            ReportGenerateMilestone(progress, log, modeTemplate, ref modePhase, GenerateModeMilestone.WarmupComplete);
                            return;
                        }

                        if (!warmupComplete)
                        {
                            return;
                        }

                        var fraction = BenchmarkModeMilestones.TokenFraction(count, numPredict);
                        var status = BenchmarkModeMilestones.TokenStatus(count, numPredict);
                        lastFraction = fraction;
                        ReportGenerateProgress(
                            progress, log, modeTemplate, ref modePhase,
                            fraction, "Generating", status);
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
                        $"gen_tokens={bench.EvalCount}; num_ctx={numCtx}; num_parallel={modeParallel}",
                        modeEnvSummary);
                    modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                    modePhase = BenchmarkProgressPhase.ModeCompleted;
                    lastFraction = 1.0;
                    ReportGenerateMilestone(
                        progress, log, modeTemplate, ref modePhase,
                        GenerateModeMilestone.Complete,
                        BenchmarkProgressFormatter.ModeCompleted(
                            modeIndex, modes.Count, mode, bench.GenerationTps, modeResult.DurationSec),
                        $"{bench.GenerationTps:F1} tok/s",
                        bench.GenerationTps);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errorMessage = OllamaOutputSanitizer.FormatException(ex);
                modeResult.Status = "Failed";
                modeResult.Error = errorMessage;
                modeResult.Notes = isEmbed
                    ? $"Embed benchmark failed for mode {mode}."
                    : $"Benchmark failed for mode {mode}.";
                modeResult.DurationSec = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

                modePhase = BenchmarkProgressPhase.ModeFailed;
                BenchmarkProgress.ReportAndLog(progress, log, modeTemplate() with
                {
                    Phase = modePhase,
                    ModeFraction = lastFraction,
                    ModeMilestone = "Failed",
                    ModeStatusDetail = "Failed",
                    DurationSec = modeResult.DurationSec,
                    Error = errorMessage,
                    LogLine = BenchmarkProgressFormatter.ModeFailed(modeIndex, modes.Count, mode, errorMessage)
                });

                await RecoverAfterModeFailureAsync(
                    modelName, mode, modeIndex, modes.Count, log, cancellationToken).ConfigureAwait(false);
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
        else
        {
            BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
            {
                Phase = BenchmarkProgressPhase.ModelCompleted,
                BenchmarkKind = benchmarkKind,
                Model = modelName,
                ModelIndex = modelIndex,
                ModelCount = modelCount,
                ModeCount = modes.Count,
                LogLine = $"{modelName} complete — all modes failed"
            });
        }

        report.CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var reportPath = Path.Combine(outputDir, "report.json");
        await JsonFileHelper.WriteAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
        try
        {
            var savedProfile = await _profiles.UpdateFromReportAsync(reportPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (savedProfile is not null)
            {
                var mismatches = ReportProfileValidator.FindMismatches(report, savedProfile);
                foreach (var mismatch in mismatches)
                {
                    BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
                    {
                        Phase = BenchmarkProgressPhase.ModelCompleted,
                        BenchmarkKind = benchmarkKind,
                        Model = modelName,
                        ModelIndex = modelIndex,
                        ModelCount = modelCount,
                        ModeCount = modes.Count,
                        LogLine = $"PROFILE_MISMATCH: {modelName} {mismatch}"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            BenchmarkProgress.ReportAndLog(progress, log, new BenchmarkProgressUpdate
            {
                Phase = BenchmarkProgressPhase.ModelCompleted,
                BenchmarkKind = benchmarkKind,
                Model = modelName,
                ModelIndex = modelIndex,
                ModelCount = modelCount,
                ModeCount = modes.Count,
                LogLine = $"Profile update warning for {modelName}: {OllamaOutputSanitizer.FormatException(ex)}"
            });
        }

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

    private static void ReportGenerateMilestone(
        IProgress<BenchmarkProgressUpdate>? progress,
        IProgress<string>? log,
        Func<BenchmarkProgressUpdate> template,
        ref BenchmarkProgressPhase phase,
        GenerateModeMilestone milestone,
        string? logLine = null,
        string? statusDetail = null,
        double? generationTps = null,
        double? embedLatencyMs = null)
    {
        phase = milestone is GenerateModeMilestone.Complete
            ? BenchmarkProgressPhase.ModeCompleted
            : milestone >= GenerateModeMilestone.WarmupStarted
                ? BenchmarkProgressPhase.ModeBenchmarking
                : BenchmarkProgressPhase.ModeApplying;

        var fraction = BenchmarkModeMilestones.GenerateFraction(milestone);
        var status = statusDetail ?? BenchmarkModeMilestones.GenerateStatus(milestone);
        BenchmarkProgress.ReportAndLog(progress, log, template() with
        {
            Phase = phase,
            ModeFraction = fraction,
            ModeMilestone = milestone.ToString(),
            ModeStatusDetail = status,
            GenerationTps = generationTps,
            EmbedLatencyMs = embedLatencyMs,
            LogLine = logLine
        });
    }

    private static void ReportGenerateProgress(
        IProgress<BenchmarkProgressUpdate>? progress,
        IProgress<string>? log,
        Func<BenchmarkProgressUpdate> template,
        ref BenchmarkProgressPhase phase,
        double fraction,
        string milestone,
        string statusDetail)
    {
        phase = BenchmarkProgressPhase.ModeBenchmarking;
        BenchmarkProgress.ReportAndLog(progress, log, template() with
        {
            Phase = phase,
            ModeFraction = fraction,
            ModeMilestone = milestone,
            ModeStatusDetail = statusDetail
        });
    }

    private static void ReportEmbedMilestone(
        IProgress<BenchmarkProgressUpdate>? progress,
        IProgress<string>? log,
        Func<BenchmarkProgressUpdate> template,
        ref BenchmarkProgressPhase phase,
        EmbedModeMilestone milestone,
        string? logLine = null,
        string? statusDetail = null,
        double? embedLatencyMs = null)
    {
        phase = milestone switch
        {
            EmbedModeMilestone.Complete => BenchmarkProgressPhase.ModeCompleted,
            EmbedModeMilestone.EmbedWarmup or EmbedModeMilestone.EmbedMeasure => BenchmarkProgressPhase.ModeBenchmarking,
            _ => BenchmarkProgressPhase.ModeApplying
        };

        var fraction = BenchmarkModeMilestones.EmbedFraction(milestone);
        var status = statusDetail ?? BenchmarkModeMilestones.EmbedStatus(milestone);
        BenchmarkProgress.ReportAndLog(progress, log, template() with
        {
            Phase = phase,
            ModeFraction = fraction,
            ModeMilestone = milestone.ToString(),
            ModeStatusDetail = status,
            EmbedLatencyMs = embedLatencyMs,
            LogLine = logLine
        });
    }

    private static string BuildModeNotes(string metrics, string? envSummary) =>
        string.IsNullOrWhiteSpace(envSummary) ? metrics : $"{metrics}; env={envSummary}";

    private async Task EnsureApiReadyForModeAsync(
        string modelName,
        ComputeMode mode,
        int modeIndex,
        int modeCount,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        if (await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        log?.Report(
            $"[{modeIndex + 1}/{modeCount}] {mode} — Ollama API not ready before benchmark; attempting recovery…");
        await RecoverAfterModeFailureAsync(modelName, mode, modeIndex, modeCount, log, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecoverAfterModeFailureAsync(
        string modelName,
        ComputeMode failedMode,
        int modeIndex,
        int modeCount,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        _lastBenchmarkMode = null;

        try
        {
            await _modelSessions.StopModelAsync(modelName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Model may not be loaded or Ollama may already be down.
        }

        try
        {
            await _modeService.Processes.StopAsync(quick: true, timeoutSec: 15, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var startup = await _modeService.Processes
                .EnsureApiReadyAsync(90, cancellationToken, autoStart: true)
                .ConfigureAwait(false);
            _apiClient.InvalidateCaches();

            if (startup.Success)
            {
                log?.Report(
                    $"[{modeIndex + 1}/{modeCount}] {failedMode} — recovery: Ollama restarted; continuing with remaining modes…");
            }
            else
            {
                log?.Report(
                    $"[{modeIndex + 1}/{modeCount}] {failedMode} — recovery warning: {startup.Message}");
            }
        }
        catch (Exception recoverEx)
        {
            log?.Report(
                $"[{modeIndex + 1}/{modeCount}] {failedMode} — recovery warning: {OllamaOutputSanitizer.FormatException(recoverEx)}");
        }
    }
}