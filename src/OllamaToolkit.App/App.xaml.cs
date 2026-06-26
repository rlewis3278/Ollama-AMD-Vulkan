using System.Windows;
using OllamaToolkit.App.Services;

namespace OllamaToolkit.App;

public partial class App : Application
{
    public static AppServices Services { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Services.Diagnostics.Write("Error", $"Unhandled UI exception: {args.Exception.Message}");
            Services.ActivityLog.Write("Error", args.Exception.Message);
            MessageBox.Show(
                args.Exception.Message,
                "Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.Diagnostics.Write("Error", $"Unobserved task exception: {args.Exception.Message}");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await Services.WorkQueue.ShutdownAsync().ConfigureAwait(false);
        Services.Dispose();
        base.OnExit(e);
    }
}