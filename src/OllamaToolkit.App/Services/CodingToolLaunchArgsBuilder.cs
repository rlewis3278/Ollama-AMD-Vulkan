namespace OllamaToolkit.App.Services;

public static class CodingToolLaunchArgsBuilder
{
    public sealed record Request(
        string ToolId,
        string Action,
        string YesChoice,
        string? Model,
        string ExtraArgChoice,
        string? CustomExtraArgs);

    public static List<string> BuildArgs(Request request)
    {
        var args = new List<string> { "launch", request.ToolId };

        if (string.Equals(request.Action, OllamaLaunchIntegrations.ActionConfigure, StringComparison.Ordinal))
        {
            args.Add("--config");
        }
        else if (string.Equals(request.Action, OllamaLaunchIntegrations.ActionRestore, StringComparison.Ordinal))
        {
            args.Add("--restore");
        }

        if (!string.Equals(request.Action, OllamaLaunchIntegrations.ActionRestore, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("--model");
            args.Add(request.Model.Trim());
        }

        if (string.Equals(request.YesChoice, OllamaLaunchIntegrations.YesOn, StringComparison.Ordinal))
        {
            args.Add("-y");
        }

        var extra = ResolveExtraArgs(request.ExtraArgChoice, request.CustomExtraArgs);
        if (extra.Count > 0)
        {
            args.Add("--");
            args.AddRange(extra);
        }

        return args;
    }

    public static string FormatCommandLine(IEnumerable<string> args) =>
        "ollama " + string.Join(' ', args.Select(QuoteForDisplay));

    private static IReadOnlyList<string> ResolveExtraArgs(string extraArgChoice, string? customExtraArgs)
    {
        if (string.Equals(extraArgChoice, OllamaLaunchIntegrations.ExtraNone, StringComparison.Ordinal))
        {
            return [];
        }

        if (string.Equals(extraArgChoice, OllamaLaunchIntegrations.ExtraCustom, StringComparison.Ordinal))
        {
            return SplitExtraArgs(customExtraArgs);
        }

        return SplitExtraArgs(extraArgChoice);
    }

    private static List<string> SplitExtraArgs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string QuoteForDisplay(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}