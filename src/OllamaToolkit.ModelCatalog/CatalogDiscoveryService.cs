using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;
using OllamaToolkit.ModelCatalog.Models;

namespace OllamaToolkit.ModelCatalog;

public sealed class CatalogDiscoveryService
{
    private static readonly string[] BaseSearchTerms =
    [
        "llm", "chat", "code", "embedding", "vision", "reasoning", "tools",
        "mixture", "moe", "instruct", "coder", "math", "agent", "multimodal"
    ];

    private readonly LibraryCatalogStoreService _catalogStore;
    private readonly AiSettingsService _settings;
    private readonly OllamaApiClient _apiClient;
    private readonly SummarizerModelResolver _summarizer;

    public CatalogDiscoveryService(
        LibraryCatalogStoreService catalogStore,
        AiSettingsService? settings = null,
        OllamaApiClient? apiClient = null,
        SummarizerModelResolver? summarizer = null)
    {
        _catalogStore = catalogStore;
        _settings = settings ?? new AiSettingsService();
        _apiClient = apiClient ?? new OllamaApiClient();
        _summarizer = summarizer ?? new SummarizerModelResolver(_settings, _apiClient);
    }

    public async Task<CatalogMergeResult> DiscoverAsync(
        string? userQuery = null,
        bool runAiSweep = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var queries = new HashSet<string>(BaseSearchTerms, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userQuery))
        {
            queries.Add(userQuery.Trim());
        }

        if (runAiSweep)
        {
            var aiTerms = await SuggestSearchTermsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var term in aiTerms)
            {
                queries.Add(term);
            }
        }

        progress?.Report($"Running discovery across {queries.Count} search term(s)...");
        return await _catalogStore.DiscoverFromSearchTermsAsync(queries.ToList(), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> SuggestSearchTermsAsync(CancellationToken cancellationToken)
    {
        if (!await _settings.IsFeatureEnabledAsync("CatalogCategorization", cancellationToken)
                .ConfigureAwait(false))
        {
            return [];
        }

        var summarizer = await _summarizer.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (summarizer is null || !await _apiClient.IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var catalog = await _catalogStore.GetEntriesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var sample = catalog
            .OrderByDescending(e => e.Name.Contains(':', StringComparison.Ordinal))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .Select(e => e.Name)
            .ToList();

        var prompt = $"""
            Suggest 12 short search keywords to find more Ollama LLM models on ollama.com that are NOT already in this sample list.
            Include community namespaces, niche domains, and trending model families.
            Reply with one keyword per line, no numbering.

            Sample catalog ({sample.Count} names):
            {string.Join(Environment.NewLine, sample)}
            """;

        try
        {
            var response = await _apiClient.GenerateAsync(summarizer, prompt, 128, 4096, cancellationToken)
                .ConfigureAwait(false);
            return response
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimStart('-', '*', ' ', '\t', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')'))
                .Where(l => l.Length is >= 2 and <= 40)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}