using OllamaToolkit.Core.EnvBackup;
using OllamaToolkit.Core.Ollama;

namespace OllamaToolkit.Core.Modes;

public sealed class ModeService
{
    private readonly ModeDefinitionService _definitions;
    private readonly EnvBackupService _envBackup;
    private readonly OllamaProcessService _processes;

    public ModeService(
        ModeDefinitionService? definitions = null,
        EnvBackupService? envBackup = null,
        OllamaProcessService? processes = null)
    {
        _definitions = definitions ?? new ModeDefinitionService();
        _envBackup = envBackup ?? new EnvBackupService();
        _processes = processes ?? new OllamaProcessService();
    }

    public ModeDefinitionService Definitions => _definitions;

    public string DetectCurrentMode()
    {
        var snapshot = _envBackup.ReadUserSnapshot();
        foreach (var mode in Enum.GetValues<ComputeMode>())
        {
            if (MatchesMode(mode, snapshot))
            {
                return mode.ToString();
            }
        }

        return "Custom/Unknown";
    }

    public async Task<ModeApplyResult> ApplyModeAsync(
        ComputeMode mode,
        bool restartOllama = true,
        bool saveBackup = true,
        IntPtr? keepFocusWindow = null,
        CancellationToken cancellationToken = default)
    {
        var definition = _definitions.Get(mode);

        if (saveBackup)
        {
            await _envBackup.SaveBackupAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in definition.Variables)
        {
            System.Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.User);
            System.Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.Process);
        }

        if (mode != ComputeMode.APU)
        {
            System.Environment.SetEnvironmentVariable("OLLAMA_NUM_GPU", null, EnvironmentVariableTarget.User);
            System.Environment.SetEnvironmentVariable("OLLAMA_NUM_GPU", null, EnvironmentVariableTarget.Process);
        }

        System.Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null, EnvironmentVariableTarget.User);
        System.Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null, EnvironmentVariableTarget.Process);

        if (restartOllama)
        {
            await _processes.RestartAsync(keepFocusWindow: keepFocusWindow, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new ModeApplyResult
        {
            Mode = mode,
            Definition = definition,
            RestartedOllama = restartOllama
        };
    }

    private bool MatchesMode(ComputeMode mode, IReadOnlyDictionary<string, string?> snapshot)
    {
        var definition = _definitions.Get(mode);
        foreach (var entry in definition.Variables)
        {
            snapshot.TryGetValue(entry.Key, out var current);
            if (!ValuesMatch(entry.Key, entry.Value, current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValuesMatch(string name, string expected, string? current)
    {
        if (string.Equals(current, expected, StringComparison.Ordinal))
        {
            return true;
        }

        if (name == "OLLAMA_IGPU_ENABLE" && expected == "0" && string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        return false;
    }
}

public sealed class ModeApplyResult
{
    public required ComputeMode Mode { get; init; }
    public required ModeDefinition Definition { get; init; }
    public bool RestartedOllama { get; init; }
}