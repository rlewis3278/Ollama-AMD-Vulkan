using System.Windows;
using OllamaToolkit.App.Services;

namespace OllamaToolkit.App;

public partial class App : Application
{
    public static AppServices Services { get; } = new();

    protected override async void OnExit(ExitEventArgs e)
    {
        await Services.WorkQueue.ShutdownAsync().ConfigureAwait(false);
        Services.Dispose();
        base.OnExit(e);
    }
}