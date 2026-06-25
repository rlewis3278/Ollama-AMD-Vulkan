using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.BenchmarkStore;

public sealed class ProfileStoreService
{
    private readonly OllamaApiClient _apiClient;
    private ModelProfileStoreDocument? _cache;

    public ProfileStoreService(OllamaApiClient? apiClient = null)
    {
        _apiClient = apiClient ?? new OllamaApiClient();
    }

    public static int GetRecommendedBenchmarkNumCtx(double sizeGb) =>
        sizeGb >= 18 ? 4096 : sizeGb >= 8 ? 6144 : 8192;

    public void ClearCache() => _cache = null;

    public async Task<ClearTestDataResult> ClearAllTestDataAsync(CancellationToken cancellationToken = default)
    {
        await SaveAsync(new ModelProfileStoreDocument(), cancellationToken).ConfigureAwait(false);

        var reportDirsRemoved = 0;
        var reportFilesRemoved = 0;
        if (Directory.Exists(ToolkitPaths.ReportsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(ToolkitPaths.ReportsRoot))
            {
                Directory.Delete(dir, recursive: true);
                reportDirsRemoved++;
            }

            foreach (var file in Directory.EnumerateFiles(ToolkitPaths.ReportsRoot, "report.json", SearchOption.TopDirectoryOnly))
            {
                File.Delete(file);
                reportFilesRemoved++;
            }
        }

        if (Directory.Exists(ToolkitPaths.GuiTestReportsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(ToolkitPaths.GuiTestReportsDir))
            {
                Directory.Delete(dir, recursive: true);
                reportDirsRemoved++;
            }

            foreach (var file in Directory.EnumerateFiles(ToolkitPaths.GuiTestReportsDir))
            {
                File.Delete(file);
                reportFilesRemoved++;
            }
        }
        else
        {
            Directory.CreateDirectory(ToolkitPaths.GuiTestReportsDir);
        }

        _apiClient.InvalidateCaches();
        return new ClearTestDataResult(reportDirsRemoved, reportFilesRemoved);
    }

    public async Task<ClearTestDataResult> ClearTestDataForModelAsync(
        string modelName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new ClearTestDataResult(0, 0);
        }

        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!store.Models.TryGetValue(modelName, out var profile))
        {
            return new ClearTestDataResult(0, 0);
        }

        var (reportDirsRemoved, reportFilesRemoved) = DeleteProfileReportArtifacts(profile);
        store.Models.Remove(modelName);
        await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        return new ClearTestDataResult(reportDirsRemoved, reportFilesRemoved);
    }

    private static (int ReportDirsRemoved, int ReportFilesRemoved) DeleteProfileReportArtifacts(ModelProfileEntry profile)
    {
        var reportDirsRemoved = 0;
        var reportFilesRemoved = 0;

        if (!string.IsNullOrWhiteSpace(profile.OutputDir) && Directory.Exists(profile.OutputDir))
        {
            Directory.Delete(profile.OutputDir, recursive: true);
            reportDirsRemoved++;
            return (reportDirsRemoved, reportFilesRemoved);
        }

        if (!string.IsNullOrWhiteSpace(profile.ReportPath))
        {
            if (File.Exists(profile.ReportPath))
            {
                File.Delete(profile.ReportPath);
                reportFilesRemoved++;
            }

            var dir = Path.GetDirectoryName(profile.ReportPath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                reportDirsRemoved++;
            }
        }

        return (reportDirsRemoved, reportFilesRemoved);
    }

    public async Task<ModelProfileStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<ModelProfileStoreDocument>(
            ToolkitPaths.ModelProfilesFile, cancellationToken).ConfigureAwait(false)
            ?? new ModelProfileStoreDocument();

        _cache.Models ??= new Dictionary<string, ModelProfileEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    public async Task SaveAsync(ModelProfileStoreDocument store, CancellationToken cancellationToken = default)
    {
        store.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = store;
        await JsonFileHelper.WriteAsync(ToolkitPaths.ModelProfilesFile, store, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OllamaModelTag>> GetLocalModelsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var tags = await _apiClient.GetTagsAsync(
            forceRefresh: forceRefresh,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return LocalModelFilter.FilterBenchmarkable(tags);
    }

    public async Task<IReadOnlyList<ModelProfileSummary>> GetAllSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var local = await GetLocalModelsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var summaries = new List<ModelProfileSummary>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in local)
        {
            seen.Add(model.Name);
            store.Models.TryGetValue(model.Name, out var profile);
            var sizeGb = Math.Round(model.Size / 1_073_741_824.0, 2);
            var digest = string.Empty; // digest from API when available

            var status = "Untested";
            var bestMode = string.Empty;
            var bestTps = 0.0;
            var bestEmbedMs = 0.0;
            var benchmarkKind = BenchmarkKinds.Generate;
            var lastTested = string.Empty;
            var needsRetest = true;

            if (profile is not null)
            {
                benchmarkKind = string.IsNullOrWhiteSpace(profile.BenchmarkKind)
                    ? BenchmarkKinds.Generate
                    : profile.BenchmarkKind;
                bestEmbedMs = profile.BestEmbedMs;

                if (!string.IsNullOrEmpty(profile.Digest) && !string.IsNullOrEmpty(digest)
                    && !string.Equals(profile.Digest, digest, StringComparison.Ordinal))
                {
                    status = "Updated - retest needed";
                }
                else if (!string.IsNullOrEmpty(profile.BestMode))
                {
                    status = "Tested";
                    bestMode = profile.BestMode;
                    bestTps = profile.BestTps;
                    lastTested = profile.LastTested ?? string.Empty;
                    needsRetest = false;
                }
                else
                {
                    status = "Test failed";
                }
            }

            summaries.Add(new ModelProfileSummary
            {
                Model = model.Name,
                SizeGB = sizeGb,
                BenchmarkKind = benchmarkKind,
                Quantization = model.Details?.QuantizationLevel ?? "-",
                ParameterSize = model.Details?.ParameterSize ?? "-",
                Digest = digest,
                Status = status,
                BestMode = bestMode,
                BestTps = bestTps,
                BestEmbedMs = bestEmbedMs,
                LastTested = lastTested,
                NeedsRetest = needsRetest,
                RecommendedCtx = GetRecommendedBenchmarkNumCtx(sizeGb),
                Results = profile?.Results
            });
        }

        foreach (var (name, profile) in store.Models)
        {
            if (seen.Contains(name))
            {
                continue;
            }

            summaries.Add(BuildSummaryFromProfile(name, profile, sizeGb: 0, digest: profile.Digest ?? string.Empty));
        }

        return summaries.OrderBy(s => s.Model).ToList();
    }

    private static ModelProfileSummary BuildSummaryFromProfile(
        string modelName,
        ModelProfileEntry? profile,
        double sizeGb,
        string digest)
    {
        var status = "Untested";
        var bestMode = string.Empty;
        var bestTps = 0.0;
        var bestEmbedMs = 0.0;
        var benchmarkKind = BenchmarkKinds.Generate;
        var lastTested = string.Empty;
        var needsRetest = true;

        if (profile is not null)
        {
            benchmarkKind = string.IsNullOrWhiteSpace(profile.BenchmarkKind)
                ? BenchmarkKinds.Generate
                : profile.BenchmarkKind;
            bestEmbedMs = profile.BestEmbedMs;

            if (!string.IsNullOrEmpty(profile.BestMode))
            {
                status = "Tested";
                bestMode = profile.BestMode;
                bestTps = profile.BestTps;
                lastTested = profile.LastTested ?? string.Empty;
                needsRetest = false;
            }
            else
            {
                status = "Test failed";
            }
        }

        return new ModelProfileSummary
        {
            Model = modelName,
            SizeGB = sizeGb,
            BenchmarkKind = benchmarkKind,
            Quantization = profile?.Quantization ?? "-",
            ParameterSize = "-",
            Digest = digest,
            Status = status,
            BestMode = bestMode,
            BestTps = bestTps,
            BestEmbedMs = bestEmbedMs,
            LastTested = lastTested,
            NeedsRetest = needsRetest,
            RecommendedCtx = GetRecommendedBenchmarkNumCtx(sizeGb > 0 ? sizeGb : 4),
            Results = profile?.Results
        };
    }

    public async Task<ModelProfileEntry?> UpdateFromReportAsync(
        string reportPath,
        int numCtx = 8192,
        string modelDigest = "",
        CancellationToken cancellationToken = default)
    {
        var profile = await ReportParser.ParseReportAsync(reportPath, numCtx, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        var report = await JsonFileHelper.ReadAsync<BenchmarkReportDocument>(reportPath, cancellationToken)
            .ConfigureAwait(false);
        if (report?.Model is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(modelDigest))
        {
            profile.Digest = modelDigest;
        }

        var store = await LoadAsync(cancellationToken).ConfigureAwait(false);
        store.Models[report.Model] = profile;
        await SaveAsync(store, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<IReadOnlyList<ModelProfileSummary>> GetUntestedAsync(
        CancellationToken cancellationToken = default) =>
        await GetLocalRetestQueueAsync(cancellationToken).ConfigureAwait(false);

    public async Task<RetestQueueBuildResult> BuildLocalRetestQueueAsync(
        CancellationToken cancellationToken = default)
    {
        _apiClient.InvalidateCaches();
        var localModels = await GetLocalModelsAsync(forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);
        var summaries = await GetAllSummariesAsync(cancellationToken).ConfigureAwait(false);
        return RetestQueueBuilder.Build(localModels, summaries);
    }

    public async Task<IReadOnlyList<ModelProfileSummary>> GetLocalRetestQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var build = await BuildLocalRetestQueueAsync(cancellationToken).ConfigureAwait(false);
        return build.Queue;
    }

    public static string SafeReportDirName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.');

    public static string NewGuiTestOutputDir(string modelName)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var safe = SafeReportDirName(modelName);
        var dir = Path.Combine(ToolkitPaths.GuiTestReportsDir, $"{stamp}_{safe}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}