using System.Security.Cryptography;
using System.Text;
using OllamaToolkit.AiAssist.Models;
using OllamaToolkit.Core;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog;

namespace OllamaToolkit.AiAssist;

public sealed record NlSearchCandidate(string Name, string Category, string ParameterSize, string ListDescription);

public sealed class NaturalLanguageSearchService
{
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;
    private NlSearchCacheDocument? _cache;

    public NaturalLanguageSearchService(
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public static bool LooksNaturalLanguage(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var q = query.Trim();
        if (q.Contains(':'))
        {
            return false;
        }

        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    public async Task<IReadOnlyList<string>> RankModelsAsync(
        string query,
        IReadOnlyList<NlSearchCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        if (!LooksNaturalLanguage(query) || candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (!await _settings.IsFeatureEnabledAsync(AiFeatureKeys.NaturalLanguageSearch, cancellationToken)
                .ConfigureAwait(false))
        {
            return Array.Empty<string>();
        }

        var key = HashQuery(query);
        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (doc.Queries.TryGetValue(key, out var cached) && cached.RankedModels.Count > 0)
        {
            return cached.RankedModels;
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<string>();
        }

        var catalog = string.Join("\n", candidates.Take(80).Select(c =>
            $"- {c.Name} | {c.Category} | {c.ParameterSize} | {c.ListDescription}"));

        var prompt = $"""
            Rank these Ollama models for the user intent. Reply with ONLY model names, one per line, best first (max 15).
            Intent: {query.Trim()}
            Models:
            {catalog}
            Ranked:
            """;

        try
        {
            var text = await _apiClient.GenerateAsync(summarizer, prompt, 256, 4096, cancellationToken)
                .ConfigureAwait(false);
            var ranked = ParseRankedNames(text, candidates);
            doc.Queries[key] = new NlSearchCacheEntry
            {
                Query = query.Trim(),
                RankedModels = ranked,
                SummaryModel = summarizer,
                GeneratedAt = DateTimeOffset.Now.ToString("o")
            };
            await SaveAsync(doc, cancellationToken).ConfigureAwait(false);
            return ranked;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static List<string> ParseRankedNames(string text, IReadOnlyList<NlSearchCandidate> candidates)
    {
        var names = new HashSet<string>(candidates.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (names.Contains(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(candidates.First(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Name);
            }
        }

        return result;
    }

    private static string HashQuery(string query)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(query.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    private async Task<NlSearchCacheDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<NlSearchCacheDocument>(
            ConfigPaths.NlSearchCacheFile, cancellationToken).ConfigureAwait(false)
            ?? new NlSearchCacheDocument();
        _cache.Queries ??= new Dictionary<string, NlSearchCacheEntry>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private async Task SaveAsync(NlSearchCacheDocument doc, CancellationToken cancellationToken)
    {
        doc.LastUpdated = DateTimeOffset.Now.ToString("o");
        ConfigPaths.EnsureConfigDirectory();
        _cache = doc;
        await JsonFileHelper.WriteAsync(ConfigPaths.NlSearchCacheFile, doc, cancellationToken)
            .ConfigureAwait(false);
    }
}