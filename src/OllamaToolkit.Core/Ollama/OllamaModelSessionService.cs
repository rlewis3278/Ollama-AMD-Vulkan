namespace OllamaToolkit.Core.Ollama;

/// <summary>
/// Loads and unloads models via Ollama CLI (ollama run / ollama stop) without restarting the Ollama server.
/// </summary>
public sealed class OllamaModelSessionService
{
    private readonly OllamaCliService _cli;
    private string? _activeModel;

    public InferenceActivityCallbacks? LoadActivity { get; set; }

    public OllamaModelSessionService(OllamaCliService? cli = null)
    {
        _cli = cli ?? new OllamaCliService();
    }

    public string? ActiveModel => _activeModel;

    public async Task SwitchToModelAsync(
        string model,
        bool warmLoad = true,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_activeModel)
            && !_activeModel.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            await StopModelAsync(_activeModel, cancellationToken).ConfigureAwait(false);
        }

        _activeModel = model;

        if (warmLoad)
        {
            using var activity = LoadActivity?.Begin();
            var result = await _cli.RunAsync(model, prompt: "ok", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to load {model} via ollama run: {result.CombinedOutput}");
            }
        }
    }

    public async Task StopActiveModelAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_activeModel))
        {
            return;
        }

        await StopModelAsync(_activeModel, cancellationToken).ConfigureAwait(false);
        _activeModel = null;
    }

    public async Task StopModelAsync(string model, CancellationToken cancellationToken = default)
    {
        var result = await _cli.StopAsync(model, cancellationToken).ConfigureAwait(false);
        if (!result.Success
            && !result.CombinedOutput.Contains("not found", StringComparison.OrdinalIgnoreCase)
            && !result.CombinedOutput.Contains("not running", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Failed to stop {model} via ollama stop: {result.CombinedOutput}");
        }

        if (_activeModel?.Equals(model, StringComparison.OrdinalIgnoreCase) == true)
        {
            _activeModel = null;
        }
    }

    public async Task UninstallModelAsync(string model, CancellationToken cancellationToken = default)
    {
        await StopModelAsync(model, cancellationToken).ConfigureAwait(false);
        var result = await _cli.RemoveAsync(model, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to remove {model} via ollama rm: {result.CombinedOutput}");
        }

        if (_activeModel?.Equals(model, StringComparison.OrdinalIgnoreCase) == true)
        {
            _activeModel = null;
        }
    }
}