using System.Windows;
using OllamaToolkit.App.Services;

namespace OllamaToolkit.App;

public partial class App : Application
{
    public static AppServices Services { get; } = new();

    private string? _lastUnhandledMessage;
    private DateTimeOffset _lastUnhandledAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var ex = args.Exception;
            var message = ex.Message;
            Services.Diagnostics.Write("Error", $"Unhandled UI exception: {message}");
            Services.Diagnostics.Write("Error", $"Stack: {ex.StackTrace}");
            if (ex.InnerException is not null)
            {
                Services.Diagnostics.Write("Error", $"Inner: {ex.InnerException.Message}");
                Services.Diagnostics.Write("Error", $"Inner stack: {ex.InnerException.StackTrace}");
            }

            Services.ActivityLog.Write("Error", message);

            var now = DateTimeOffset.UtcNow;
            var isRepeat = string.Equals(_lastUnhandledMessage, message, StringComparison.Ordinal)
                && (now - _lastUnhandledAt).TotalSeconds < 2;
            _lastUnhandledMessage = message;
            _lastUnhandledAt = now;

            if (!isRepeat)
            {
                MessageBox.Show(
                    message,
                    "Unexpected Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

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
        try
        {
            await Services.ModelSessions.StopActiveModelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort — app is closing.
        }

        await Services.WorkQueue.ShutdownAsync().ConfigureAwait(false);
        Services.Dispose();
        base.OnExit(e);
    }
}