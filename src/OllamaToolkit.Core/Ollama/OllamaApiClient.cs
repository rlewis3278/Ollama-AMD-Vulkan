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

    public InferenceActivityCallbacks? BenchmarkInferenceActivity { get; set; }
    public InferenceActivityCallbacks? ChatInferenceActivity { get; set; }

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

        var attempts = forceRefresh ? 3 : 1;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                var response = await _httpClient.GetFromJsonAsync<TagsResponse>(
                    $"{_host}/api/tags", JsonFileHelper.Options, cts.Token).ConfigureAwait(false);
                _tagsCache = response?.Models ?? new List<OllamaModelTag>();
                _tagsCheckedAt = DateTime.UtcNow;
                if (_tagsCache.Count > 0)
                {
                    _readyCached = true;
                    _readyCheckedAt = DateTime.UtcNow;
                }

                return _tagsCache;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            System.Diagnostics.Debug.WriteLine($"GetTagsAsync failed after {attempts} attempt(s): {lastError.Message}");
        }

        if (_tagsCache is not null)
        {
            _tagsCheckedAt = DateTime.UtcNow;
            return _tagsCache;
        }

        return Array.Empty<OllamaModelTag>();
    }

    public async Task<OllamaTagsSnapshot> FetchTagsSnapshotAsync(
        int timeoutSec = 10,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _tagsCache is not null && (DateTime.UtcNow - _tagsCheckedAt).TotalSeconds < 15)
        {
            return new OllamaTagsSnapshot
            {
                Reachable = _readyCached,
                Tags = _tagsCache
            };
        }

        var tags = await GetTagsAsync(timeoutSec, ttlSec: 0, forceRefresh, cancellationToken)
            .ConfigureAwait(false);
        return new OllamaTagsSnapshot
        {
            Reachable = _readyCached,
            Tags = tags
        };
    }

    public void InvalidateCaches()
    {
        _tagsCache = null;
        _tagsCheckedAt = DateTime.MinValue;
        _readyCached = false;
        _readyCheckedAt = DateTime.MinValue;
    }

    private async Task<HttpResponseMessage> SendPullRequestAsync(
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = new { name = model, stream = true };
            var json = JsonSerializer.Serialize(body, JsonFileHelper.Options);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/api/pull") { Content = content };
            return await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (OllamaConnectionHelper.IsConnectionError(ex))
        {
            InvalidateCaches();
            throw new InvalidOperationException(OllamaConnectionHelper.FormatUserMessage(ex), ex);
        }
    }

    public async Task<BenchmarkEmbedResult> BenchmarkEmbedAsync(
        string model,
        string input,
        bool warmup = true,
        IProgress<string>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = BenchmarkInferenceActivity?.Begin();
        if (warmup)
        {
            stageProgress?.Report("warmup");
            await EmbedRawAsync(model, "warmup", cancellationToken).ConfigureAwait(false);
        }

        stageProgress?.Report("measure");
        var raw = await EmbedRawAsync(model, input, cancellationToken).ConfigureAwait(false);
        var totalSeconds = raw.TotalDurationNs / 1_000_000_000.0;
        var latencyMs = raw.TotalDurationNs / 1_000_000.0;
        var promptTps = totalSeconds > 0 && raw.PromptEvalCount > 0
            ? raw.PromptEvalCount / totalSeconds
            : 0;

        return new BenchmarkEmbedResult
        {
            LatencyMs = Math.Round(latencyMs, 2),
            PromptEvalTps = Math.Round(promptTps, 2),
            PromptEvalCount = raw.PromptEvalCount,
            Dimensions = raw.Embeddings?.FirstOrDefault()?.Count ?? 0
        };
    }

    private async Task<EmbedRawResponse> EmbedRawAsync(
        string model,
        string input,
        CancellationToken cancellationToken)
    {
        var body = new { model, input };
        using var response = await _httpClient.PostAsJsonAsync($"{_host}/api/embed", body, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbedRawResponse>(JsonFileHelper.Options, cancellationToken)
            .ConfigureAwait(false);
        return payload ?? new EmbedRawResponse();
    }

    public async Task<BenchmarkGenerateResult> BenchmarkGenerateAsync(
        string model,
        string prompt,
        int numPredict = 32,
        int numCtx = 8192,
        bool warmup = true,
        IProgress<int>? tokenProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = BenchmarkInferenceActivity?.Begin();
        if (warmup)
        {
            tokenProgress?.Report(0);
            await GenerateRawAsync(model, "ok", 8, numCtx, cancellationToken).ConfigureAwait(false);
            tokenProgress?.Report(Math.Max(1, (int)(numPredict * 0.1)));
        }

        try
        {
            return await BenchmarkGenerateStreamingAsync(
                model, prompt, numPredict, numCtx, tokenProgress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var raw = await GenerateRawAsync(model, prompt, numPredict, numCtx, cancellationToken)
                .ConfigureAwait(false);
            tokenProgress?.Report(raw.EvalCount);
            return ToBenchmarkGenerateResult(raw);
        }
    }

    public async Task<BenchmarkGenerateResult> BenchmarkGenerateStreamingAsync(
        string model,
        string prompt,
        int numPredict,
        int numCtx,
        IProgress<int>? tokenProgress = null,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            model,
            prompt,
            stream = true,
            options = new { num_predict = numPredict, num_ctx = numCtx, temperature = 0.2 }
        };

        var json = JsonSerializer.Serialize(body, JsonFileHelper.Options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host}/api/generate") { Content = content };
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var responseText = new StringBuilder();
        GenerateRawResponse? final = null;
        var lastReported = -1;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            GenerateStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<GenerateStreamChunk>(line, JsonFileHelper.Options);
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrEmpty(chunk?.Response))
            {
                responseText.Append(chunk.Response);
            }

            if (chunk?.EvalCount is > 0 && chunk.EvalCount != lastReported)
            {
                lastReported = chunk.EvalCount;
                tokenProgress?.Report(chunk.EvalCount);
            }

            if (chunk?.Done == true)
            {
                final = new GenerateRawResponse
                {
                    Response = responseText.ToString(),
                    EvalCount = chunk.EvalCount,
                    EvalDurationNs = chunk.EvalDurationNs,
                    PromptEvalCount = chunk.PromptEvalCount,
                    PromptEvalDurationNs = chunk.PromptEvalDurationNs
                };
                break;
            }
        }

        final ??= new GenerateRawResponse { Response = responseText.ToString() };
        tokenProgress?.Report(final.EvalCount > 0 ? final.EvalCount : numPredict);
        return ToBenchmarkGenerateResult(final);
    }

    private static BenchmarkGenerateResult ToBenchmarkGenerateResult(GenerateRawResponse raw)
    {
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
        using var activity = ChatInferenceActivity?.Begin();
        await foreach (var chunk in ChatStreamCoreAsync(model, messages, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> ChatStreamCoreAsync(
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
        using var response = await SendPullRequestAsync(model, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var tracker = new PullProgressTracker();

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
                progress?.Report(tracker.Update(chunk));
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

    private sealed class GenerateStreamChunk
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

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

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("completed")]
        public long? Completed { get; set; }

        [JsonPropertyName("total")]
        public long? Total { get; set; }
    }

    private sealed class PullProgressTracker
    {
        private readonly Dictionary<string, LayerProgress> _layers = new(StringComparer.Ordinal);

        public ModelPullProgress Update(PullChunk chunk)
        {
            if (chunk.Total is > 0 || chunk.Completed is >= 0)
            {
                var key = ResolveLayerKey(chunk);
                if (!_layers.TryGetValue(key, out var layer))
                {
                    layer = new LayerProgress();
                    _layers[key] = layer;
                }

                if (chunk.Total is > 0)
                {
                    layer.Total = chunk.Total.Value;
                }

                if (chunk.Completed is >= 0)
                {
                    layer.Completed = chunk.Completed.Value;
                }
            }

            var (completedBytes, totalBytes, percent) = ComputeAggregateProgress(chunk);
            return new ModelPullProgress
            {
                Status = chunk.Status ?? string.Empty,
                Percent = percent,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes
            };
        }

        private (long? CompletedBytes, long? TotalBytes, int? Percent) ComputeAggregateProgress(PullChunk chunk)
        {
            long totalBytes = 0;
            long completedBytes = 0;
            foreach (var layer in _layers.Values)
            {
                if (layer.Total <= 0)
                {
                    continue;
                }

                totalBytes += layer.Total;
                completedBytes += Math.Min(layer.Completed, layer.Total);
            }

            if (totalBytes > 0)
            {
                var pct = (int)Math.Clamp(100.0 * completedBytes / totalBytes, 0, 100);
                return (completedBytes, totalBytes, pct);
            }

            if (chunk.Total is > 0 && chunk.Completed is >= 0)
            {
                var pct = (int)Math.Clamp(100.0 * chunk.Completed.Value / chunk.Total.Value, 0, 100);
                return (chunk.Completed.Value, chunk.Total.Value, pct);
            }

            return (null, null, null);
        }

        private static string ResolveLayerKey(PullChunk chunk)
        {
            if (!string.IsNullOrWhiteSpace(chunk.Digest))
            {
                return chunk.Digest;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Status))
            {
                var shaIndex = chunk.Status.IndexOf("sha256:", StringComparison.OrdinalIgnoreCase);
                if (shaIndex >= 0)
                {
                    return chunk.Status[shaIndex..].Trim();
                }

                return chunk.Status;
            }

            return "layer";
        }

        private sealed class LayerProgress
        {
            public long Completed { get; set; }
            public long Total { get; set; }
        }
    }
}

public sealed class OllamaTagsSnapshot
{
    public bool Reachable { get; init; }
    public IReadOnlyList<OllamaModelTag> Tags { get; init; } = Array.Empty<OllamaModelTag>();
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

    [JsonPropertyName("remote_host")]
    public string? RemoteHost { get; set; }
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

public sealed class BenchmarkEmbedResult
{
    public double LatencyMs { get; init; }
    public double PromptEvalTps { get; init; }
    public int PromptEvalCount { get; init; }
    public int Dimensions { get; init; }
}

public sealed class EmbedRawResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("embeddings")]
    public List<List<double>>? Embeddings { get; set; }

    [JsonPropertyName("total_duration")]
    public long TotalDurationNs { get; set; }

    [JsonPropertyName("load_duration")]
    public long LoadDurationNs { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int PromptEvalCount { get; set; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}