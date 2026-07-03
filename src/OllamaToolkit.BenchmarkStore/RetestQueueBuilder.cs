using OllamaToolkit.BenchmarkStore.Models;
using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.BenchmarkStore;

public sealed class RetestQueueModelDecision
{
    public required string Model { get; init; }
    public bool Included { get; init; }
    public required string Reason { get; init; }
    public bool NeedsRetest { get; init; }
    public int ResultCount { get; init; }
}

public sealed class RetestQueueBuildResult
{
    public IReadOnlyList<ModelProfileSummary> Queue { get; init; } = [];
    public IReadOnlyList<RetestQueueModelDecision> Decisions { get; init; } = [];
    public int RawTagCount { get; init; }
    public int LocalModelCount { get; init; }
    public int ProfileCount { get; init; }
    public bool ApiReachable { get; init; }
    public string? TagsError { get; init; }
    public int IncludedCount => Queue.Count;
    public int ExcludedCount => Decisions.Count(d => !d.Included);
}

public static class RetestQueueBuilder
{
    public static RetestQueueBuildResult Build(
        IReadOnlyList<OllamaModelTag> localModels,
        IReadOnlyList<ModelProfileSummary> summaries)
    {
        var summaryByModel = summaries
            .GroupBy(s => s.Model, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var decisions = new List<RetestQueueModelDecision>();
        var queue = new List<ModelProfileSummary>();

        foreach (var model in localModels)
        {
            if (!summaryByModel.TryGetValue(model.Name, out var summary))
            {
                summary = new ModelProfileSummary
                {
                    Model = model.Name,
                    SizeGB = Math.Round(model.Size / 1_073_741_824.0, 2),
                    NeedsRetest = true,
                    RecommendedCtx = ProfileStoreService.GetRecommendedBenchmarkNumCtx(
                        Math.Round(model.Size / 1_073_741_824.0, 2))
                };
            }

            var resultCount = summary.Results?.Count ?? 0;
            if (ShouldInclude(summary, out var reason))
            {
                decisions.Add(new RetestQueueModelDecision
                {
                    Model = summary.Model,
                    Included = true,
                    Reason = reason,
                    NeedsRetest = summary.NeedsRetest,
                    ResultCount = resultCount
                });
                queue.Add(summary);
            }
            else
            {
                decisions.Add(new RetestQueueModelDecision
                {
                    Model = summary.Model,
                    Included = false,
                    Reason = reason,
                    NeedsRetest = summary.NeedsRetest,
                    ResultCount = resultCount
                });
            }
        }

        return new RetestQueueBuildResult
        {
            Queue = queue.OrderBy(s => s.Model, StringComparer.OrdinalIgnoreCase).ToList(),
            Decisions = decisions,
            LocalModelCount = localModels.Count
        };
    }

    public static bool ShouldInclude(ModelProfileSummary summary, out string reason)
    {
        if (BenchmarkCompletion.IsFullyTested(summary))
        {
            reason = "FullyTested";
            return false;
        }

        if (summary.NeedsRetest)
        {
            reason = "NeedsRetest";
            return true;
        }

        if (BenchmarkCompletion.HasFailedModeResults(summary))
        {
            reason = "FailedModeResults";
            return true;
        }

        var resultCount = summary.Results?.Count ?? 0;
        if (resultCount > 0 && resultCount < BenchmarkCompletion.RequiredModeCount)
        {
            reason = $"PartialResults({resultCount}/{BenchmarkCompletion.RequiredModeCount})";
            return true;
        }

        reason = "NoResultsNeeded";
        return false;
    }
}