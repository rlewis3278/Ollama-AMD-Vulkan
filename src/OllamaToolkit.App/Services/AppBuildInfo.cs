using System.IO;
using System.Reflection;

namespace OllamaToolkit.App.Services;

public static class AppBuildInfo
{
    public static string GetDisplayRevision()
    {
        var (version, hash) = ParseInformationalVersion();
        return string.IsNullOrWhiteSpace(hash) || hash.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? version
            : $"{version} · {hash}";
    }

    public static string GetShortFooterLabel()
    {
        var (version, hash) = ParseInformationalVersion();
        return string.IsNullOrWhiteSpace(hash) || hash.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? $"v{version}"
            : $"v{version} ({hash})";
    }

    public static string GetDiagnosticStamp()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var stamp = "unknown";
        if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
        {
            stamp = File.GetLastWriteTime(assemblyPath).ToString("yyyy-MM-dd HH:mm:ss");
        }

        return $"Build stamp: {GetDisplayRevision()}, DLL {stamp}";
    }

    private static (string Version, string Hash) ParseInformationalVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.1.0";

        var plus = informational.IndexOf('+');
        if (plus < 0)
        {
            return (informational, string.Empty);
        }

        return (informational[..plus], informational[(plus + 1)..]);
    }
}