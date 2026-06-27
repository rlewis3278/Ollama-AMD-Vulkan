namespace OllamaToolkit.Core.Settings;

public sealed class AiSettingsService
{
    private AiSettingsDocument? _cache;

    public async Task<AiSettingsDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        ConfigPaths.EnsureConfigDirectory();
        _cache = await JsonFileHelper.ReadAsync<AiSettingsDocument>(ConfigPaths.AiSettingsFile, cancellationToken)
            .ConfigureAwait(false)
            ?? new AiSettingsDocument();

        _cache.FeatureFlags ??= AiSettingsDocument.CreateDefaultFeatureFlags();
        if (_cache.FeatureFlags.Count == 0)
        {
            _cache.FeatureFlags = AiSettingsDocument.CreateDefaultFeatureFlags();
        }

        return _cache;
    }

    public async Task SaveAsync(AiSettingsDocument settings, CancellationToken cancellationToken = default)
    {
        ConfigPaths.EnsureConfigDirectory();
        _cache = settings;
        await JsonFileHelper.WriteAsync(ConfigPaths.AiSettingsFile, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsFeatureEnabledAsync(string featureKey, CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.ToolkitAiEnabled)
        {
            return false;
        }

        return settings.FeatureFlags.TryGetValue(featureKey, out var enabled) && enabled;
    }
}