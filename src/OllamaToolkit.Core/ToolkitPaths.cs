namespace OllamaToolkit.Core;

public static class ToolkitPaths
{
    private static string? _toolkitRoot;

    public static string ToolkitRoot
    {
        get
        {
            if (_toolkitRoot is not null)
            {
                return _toolkitRoot;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OllamaToolkit.sln"))
                    || Directory.Exists(Path.Combine(dir.FullName, "reports")))
                {
                    _toolkitRoot = dir.FullName;
                    return _toolkitRoot;
                }

                dir = dir.Parent;
            }

            _toolkitRoot = Directory.GetCurrentDirectory();
            return _toolkitRoot;
        }
    }

    public static string ReportsRoot => Path.Combine(ToolkitRoot, "reports");
    public static string GuiTestReportsDir => Path.Combine(ReportsRoot, "gui-tests");
    public static string ModelProfilesFile => Path.Combine(ConfigPaths.ConfigDirectory, "model-profiles.json");
}