namespace OllamaToolkit.App.Services;

/// <summary>
/// Integrations exposed by <c>ollama launch</c> (see <c>ollama launch --help</c>).
/// </summary>
public static class OllamaLaunchIntegrations
{
    public sealed record Integration(
        string Id,
        string Label,
        string Icon,
        string Description,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> ExtraArgPresets);

    public const string ActionLaunch = "Launch";
    public const string ActionConfigure = "Configure (--config)";
    public const string ActionRestore = "Restore (--restore)";

    public const string YesOff = "No";
    public const string YesOn = "Yes (-y)";

    public const string ExtraNone = "(none)";
    public const string ExtraCustom = "Custom…";

    public static IReadOnlyList<Integration> All { get; } =
    [
        new("claude", "Claude Code", "🟠", "Anthropic Claude Code CLI", [], []),
        new("codex-app", "Codex App", "📱", "Codex desktop application", ["codex-desktop", "codex-gui"], []),
        new("hermes", "Hermes Agent", "🦉", "Hermes terminal agent", [], []),
        new("openclaw", "OpenClaw", "🦞", "OpenClaw agent", ["clawdbot", "moltbot"], []),
        new("opencode", "OpenCode", "⌨", "OpenCode coding agent", [], []),
        new("codex", "Codex", "💻", "OpenAI Codex CLI", [], ["--sandbox workspace-write"]),
        new("hermes-desktop", "Hermes Desktop", "🖥", "Hermes desktop app", [], []),
        new("copilot", "Copilot CLI", "🤖", "GitHub Copilot CLI", ["copilot-cli"], []),
        new("omp", "OMP", "⚙", "OMP integration", [], []),
        new("droid", "Droid", "🤖", "Factory Droid agent", [], []),
        new("kimi", "Kimi Code CLI", "🌙", "Moonshot Kimi coding CLI", [], []),
        new("pi", "Pi", "π", "Pi coding agent", [], []),
        new("pool", "Pool", "🏊", "Pool integration", [], []),
        new("cline", "Cline", "✦", "Cline VS Code agent", [], []),
        new("qwen", "Qwen Code", "🐉", "Alibaba Qwen coding CLI", [], []),
        new("vscode", "VS Code", "📝", "Visual Studio Code", ["code"], [])
    ];

    public static IReadOnlyList<string> ActionChoices { get; } =
        [ActionLaunch, ActionConfigure, ActionRestore];

    public static IReadOnlyList<string> YesChoices { get; } = [YesOff, YesOn];

    public static IReadOnlyList<string> BuildExtraArgChoices(Integration integration)
    {
        var choices = new List<string> { ExtraNone };
        foreach (var preset in integration.ExtraArgPresets)
        {
            if (!choices.Contains(preset, StringComparer.Ordinal))
            {
                choices.Add(preset);
            }
        }

        choices.Add(ExtraCustom);
        return choices;
    }
}