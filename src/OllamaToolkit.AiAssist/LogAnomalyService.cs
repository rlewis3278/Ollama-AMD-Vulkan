using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;
using OllamaToolkit.AiAssist.Models;

namespace OllamaToolkit.AiAssist;

public sealed class LogAnomalyService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;

    public LogAnomalyService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task<LogAnomaliesDocument> ScanAsync(CancellationToken cancellationToken = default)
    {
        var doc = new LogAnomaliesDocument();
        if (!File.Exists(ConfigPaths.GuiAiActivityLog))
        {
            return doc;
        }

        var lines = await File.ReadAllLinesAsync(ConfigPaths.GuiAiActivityLog, cancellationToken)
            .ConfigureAwait(false);
        var tail = lines.Length > 2000 ? lines[^2000..] : lines;

        var apuFails = tail.Count(l => l.Contains("APU", StringComparison.OrdinalIgnoreCase)
            && l.Contains("FAIL", StringComparison.OrdinalIgnoreCase));
        if (apuFails >= 3)
        {
            doc.Anomalies.Add(new LogAnomalyEntry
            {
                Pattern = "Repeated APU FAIL",
                Count = apuFails,
                Summary = "Multiple APU benchmark failures detected; try GPU mode or lower num_ctx."
            });
        }

        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.LogAnomalyDetection, cancellationToken)
                .ConfigureAwait(false))
        {
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
            return doc;
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
            return doc;
        }

        if (doc.Anomalies.Count > 0)
        {
            try
            {
                var prompt = $"""
                    Summarize these log anomaly patterns for an Ollama AMD Vulkan toolkit user in one sentence each.
                    {string.Join(Environment.NewLine, doc.Anomalies.Select(a => $"{a.Pattern} x{a.Count}"))}
                    """;
                var summary = await _apiClient.GenerateAsync(summarizer, prompt, 96, 4096, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summary) && doc.Anomalies.Count > 0)
                {
                    doc.Anomalies[0].Summary = summary.Trim();
                }
            }
            catch
            {
                // Keep heuristic summary.
            }
        }

        await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
        return doc;
    }

    public async Task<LogAnomaliesDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        ConfigPaths.EnsureConfigDirectory();
        return await JsonFileHelper.ReadAsync<LogAnomaliesDocument>(
            ConfigPaths.LogAnomaliesFile, cancellationToken).ConfigureAwait(false)
            ?? new LogAnomaliesDocument();
    }

    private static async Task SaveAsync(LogAnomaliesDocument doc, CancellationToken cancellationToken)
    {
        doc.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        await JsonFileHelper.WriteAsync(ConfigPaths.LogAnomaliesFile, doc, cancellationToken)
            .ConfigureAwait(false);
    }
}