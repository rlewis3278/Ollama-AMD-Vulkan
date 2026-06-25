using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaToolkit.Core.Ollama;

public sealed class OllamaApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _host;
    private DateTime _readyCheckedAt = DateTime.MinValue;
    private bool _readyCached;
    private DateTime _tagsCheckedAt = DateTime.MinValue;
    private IReadOnlyList<OllamaModelTag>? _tagsCache;

    public OllamaApiClient(string? host = null, HttpClient? httpClient = null)
    {
        _host = (host ?? ConfigPaths.DefaultOllamaHost).TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_host}/api/tags", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsReadyCachedAsync(int ttlSec = 10, CancellationToken cancellationToken = default)
    {
        if ((DateTime.UtcNow - _readyCheckedAt).TotalSeconds < ttlSec)
        {
            return _readyCached;
        }

        _readyCached = await IsReadyAsync(cancellationToken).ConfigureAwait(false);
        _readyCheckedAt = DateTime.UtcNow;
        return _readyCached;
    }

    public async Task<IReadOnlyList<OllamaModelTag>> GetTagsAsync(
        int timeoutSec = 10,
        int ttlSec = 15,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _tagsCache is not null && (DateTime.UtcNow - _tagsCheckedAt).TotalSeconds < ttlSec)
        {
            return _tagsCache;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            var response = await _httpClient.GetFromJsonAsync<TagsResponse>(
                $"{_host}/api/tags", JsonFileHelper.Options, cts.Token).ConfigureAwait(false);
            _tagsCache = response?.Models is { Count: > 0 } models
                ? models
                : new List<OllamaModelTag>();
            if (_tagsCache.Count > 0)
            {
                _readyCached = true;
                _readyCheckedAt = DateTime.UtcNow;
            }
        }
        catch
        {
            _tagsCache = new List<OllamaModelTag>();
        }

        _tagsCheckedAt = DateTime.UtcNow;
        return _tagsCache;
    }

    public async Task<BenchmarkGenerateResult> BenchmarkGenerateAsync(
        string model,
        string prompt,
        int numPredict = 32,
        int numCtx = 8192,
        bool warmup = true,
        CancellationToken cancellationToken = default)
    {
        if (warmup)
        {
            await GenerateRawAsync(model, "ok", 8, numCtx, cancellationToken).ConfigureAwait(false);
        }

        var raw = await GenerateRawAsync(model, prompt, numPredict, numCtx, cancellationToken).ConfigureAwait(false);
        var evalSeconds = raw.EvalDurationNs / 1_000_000_000.0;
        var promptSeconds = raw.PromptEvalDurationNs / 1_000_000_000.0;
        var generationTps = evalSeconds > 0 ? raw.EvalCount / evalSeconds : 0;
        var promptTps = promptSeconds > 0 ? raw.PromptEvalCount / promptSeconds : 0;
        var ttftMs = raw.PromptEvalDurationNs / 1_000_000.0;

        return new BenchmarkGenerateResult
        {
            Response = raw.Response,
            GenerationTps = Math.Round(generationTps, 2),
            PromptEvalTps = Math.Round(promptTps, 2),
            TtftMs = Math.Round(ttftMs, 2),
            EvalCount = raw.EvalCount
        };
    }

    public async Task<string> GenerateAsync(
        string model,
        string prompt,
        int maxPredict = 96,
        int numCtx = 4096,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            model,
            prompt,
            stream = false,
            options = new { num_predict = maxPredict, num_ctx = numCtx, temperature = 0.2 }
        };

        var raw = await GenerateRawAsync(model, prompt, maxPredict, numCtx, cancellationToken).ConfigureAwait(false);
        return raw.Response;
    }

    private async Task<GenerateRawResponse> GenerateRawAsync(
        string model,
        string prompt,
        int maxPredict,
        int numCtx,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            model,
            prompt,
            stream = false,
            options = new { num_predict = maxPredict, num_ctx = numCtx, temperature = 0.2 }
        };

        using var response = await _httpClient.PostAsJsonAsync($"{_host}/api/generate", body, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<GenerateRawResponse>(JsonFileHelper.Options, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new GenerateRawResponse();
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new { model, messages, stream = true };
        var json = JsonSerializer.Serialize(body, JsonFileHelper.Options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/api/chat") { Content = content };
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(line, JsonFileHelper.Options);
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrEmpty(chunk?.Message?.Content))
            {
                yield return chunk.Message.Content;
            }

            if (chunk?.Done == true)
            {
                yield break;
            }
        }
    }

    public async Task<bool> IsModelInstalledAsync(string model, CancellationToken cancellationToken = default)
    {
        var tags = await GetTagsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tags.Any(t => t.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
    }

    public async Task PullAsync(
        string model,
        IProgress<ModelPullProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var body = new { name = model, stream = true };
        var json = JsonSerializer.Serialize(body, JsonFileHelper.Options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/api/pull") { Content = content };
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PullChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<PullChunk>(line, JsonFileHelper.Options);
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrEmpty(chunk?.Error))
            {
                throw new InvalidOperationException(chunk.Error);
            }

            if (!string.IsNullOrEmpty(chunk?.Status))
            {
                int? percent = null;
                if (chunk.Total is > 0 && chunk.Completed is >= 0)
                {
                    percent = (int)Math.Clamp(100.0 * chunk.Completed.Value / chunk.Total.Value, 0, 100);
                }

                progress?.Report(new ModelPullProgress
                {
                    Status = chunk.Status,
                    Percent = percent
                });
            }

            if (chunk?.Status?.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
            {
                _tagsCache = null;
                return;
            }
        }
    }

    public async Task DeleteAsync(string model, CancellationToken cancellationToken = default)
    {
        var body = new { name = model };
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_host}/api/delete")
        {
            Content = JsonContent.Create(body, options: JsonFileHelper.Options)
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _tagsCache = null;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelTag>? Models { get; set; }
    }

    private sealed class GenerateRawResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long EvalDurationNs { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonPropertyName("prompt_eval_duration")]
        public long PromptEvalDurationNs { get; set; }
    }

    private sealed class ChatStreamChunk
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    private sealed class PullChunk
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("completed")]
        public long? Completed { get; set; }

        [JsonPropertyName("total")]
        public long? Total { get; set; }
    }
}

public sealed class OllamaModelTag
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modified_at")]
    public DateTimeOffset? ModifiedAt { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetails? Details { get; set; }
}

public sealed class OllamaModelDetails
{
    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("family")]
    public string? Family { get; set; }
}

public sealed class BenchmarkGenerateResult
{
    public string Response { get; init; } = string.Empty;
    public double GenerationTps { get; init; }
    public double PromptEvalTps { get; init; }
    public double TtftMs { get; init; }
    public int EvalCount { get; init; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}