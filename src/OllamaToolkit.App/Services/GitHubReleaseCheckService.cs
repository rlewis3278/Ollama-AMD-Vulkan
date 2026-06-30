using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace OllamaToolkit.App.Services;

public sealed class GitHubReleaseCheckService
{
    private const string ReleasesApi =
        "https://api.github.com/repos/rlewis3278/Ollama-AMD-Vulkan/releases/latest";

    private readonly HttpClient _httpClient;

    public GitHubReleaseCheckService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OllamaToolkit/1.3");
        }
    }

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<ReleaseCheckResult> CheckForNewerReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(ReleasesApi, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ReleaseCheckResult.Unavailable($"HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            var latest = tag.TrimStart('v', 'V');
            var current = CurrentVersion;
            if (!Version.TryParse(latest, out var latestVersion)
                || !Version.TryParse(current, out var currentVersion))
            {
                return ReleaseCheckResult.Unavailable("Could not parse version numbers.");
            }

            if (latestVersion <= currentVersion)
            {
                return ReleaseCheckResult.UpToDate(current);
            }

            var pageUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? urlEl.GetString() ?? $"https://github.com/rlewis3278/Ollama-AMD-Vulkan/releases/tag/{tag}"
                : $"https://github.com/rlewis3278/Ollama-AMD-Vulkan/releases/tag/{tag}";

            return ReleaseCheckResult.NewerAvailable(current, latest, pageUrl);
        }
        catch (Exception ex)
        {
            return ReleaseCheckResult.Unavailable(ex.Message);
        }
    }

    public sealed record ReleaseCheckResult(
        bool Checked,
        bool UpdateAvailable,
        string CurrentVersion,
        string? LatestVersion,
        string? ReleasePageUrl,
        string? Error)
    {
        public static ReleaseCheckResult UpToDate(string current) =>
            new(true, false, current, null, null, null);

        public static ReleaseCheckResult NewerAvailable(string current, string latest, string pageUrl) =>
            new(true, true, current, latest, pageUrl, null);

        public static ReleaseCheckResult Unavailable(string error) =>
            new(false, false, string.Empty, null, null, error);
    }
}