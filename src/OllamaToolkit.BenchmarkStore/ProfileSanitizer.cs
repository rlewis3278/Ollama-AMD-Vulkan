using OllamaToolkit.BenchmarkStore.Models;

namespace OllamaToolkit.BenchmarkStore;

public static class ProfileSanitizer
{
    public static readonly string[] SupportedModes = ["CPU", "APU", "GPU", "Hybrid"];

    public static bool IsSupportedMode(string? mode) =>
        !string.IsNullOrWhiteSpace(mode)
        && SupportedModes.Contains(mode, StringComparer.OrdinalIgnoreCase);

    public static bool SanitizeDocument(ModelProfileStoreDocument store)
    {
        var changed = false;
        foreach (var (name, profile) in store.Models.ToList())
        {
            if (SanitizeEntry(profile))
            {
                changed = true;
            }

            if (string.IsNullOrEmpty(profile.BestMode)
                && (profile.Results is null || profile.Results.Count == 0))
            {
                store.Models.Remove(name);
                changed = true;
            }
        }

        return changed;
    }

    public static bool SanitizeEntry(ModelProfileEntry profile)
    {
        var changed = false;

        if (profile.Results is not null)
        {
            foreach (var mode in profile.Results.Keys.ToList())
            {
                if (!IsSupportedMode(mode))
                {
                    profile.Results.Remove(mode);
                    changed = true;
                }
            }
        }

        if (profile.NumParallelByMode is not null)
        {
            foreach (var mode in profile.NumParallelByMode.Keys.ToList())
            {
                if (!IsSupportedMode(mode))
                {
                    profile.NumParallelByMode.Remove(mode);
                    changed = true;
                }
            }
        }

        if (!IsSupportedMode(profile.BestMode))
        {
            profile.BestMode = null;
            profile.BestTps = 0;
            profile.BestEmbedMs = 0;
            changed = true;
        }

        if (string.IsNullOrEmpty(profile.BestMode))
        {
            if (TryRecomputeWinner(profile, out var winnerMode, out var bestTps, out var bestEmbedMs))
            {
                profile.BestMode = winnerMode;
                profile.BestTps = bestTps;
                profile.BestEmbedMs = bestEmbedMs;
                changed = true;
            }
        }

        return changed;
    }

    public static bool TryRecomputeWinner(
        ModelProfileEntry profile,
        out string winnerMode,
        out double bestTps,
        out double bestEmbedMs)
    {
        winnerMode = string.Empty;
        bestTps = 0;
        bestEmbedMs = 0;

        if (profile.Results is null || profile.Results.Count == 0)
        {
            return false;
        }

        var isEmbed = profile.BenchmarkKind.Equals(BenchmarkKinds.Embed, StringComparison.OrdinalIgnoreCase);
        var successes = profile.Results
            .Where(kv => IsSupportedMode(kv.Key)
                         && kv.Value.Status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (successes.Count == 0)
        {
            return false;
        }

        KeyValuePair<string, ModeResultEntry> winner;
        if (isEmbed)
        {
            winner = successes.OrderBy(kv => kv.Value.EmbedLatencyMs).First();
            bestEmbedMs = winner.Value.EmbedLatencyMs;
        }
        else
        {
            winner = successes.OrderByDescending(kv => kv.Value.GenerationTps).First();
            bestTps = winner.Value.GenerationTps;
        }

        winnerMode = winner.Key;
        return true;
    }
}