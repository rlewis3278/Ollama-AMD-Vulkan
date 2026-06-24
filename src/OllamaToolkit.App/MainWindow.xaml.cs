using System.Windows;
using OllamaToolkit.App.Services;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Settings;

namespace OllamaToolkit.App;

public partial class MainWindow : Window
{
    private readonly BackgroundWorkQueue _workQueue = new();
    private readonly AiSettingsService _aiSettings = new();
    private readonly OllamaApiClient _apiClient = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoadedAsync;
        Closed += OnClosedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        await _workQueue.EnqueueAsync(async ct =>
        {
            var ready = await _apiClient.IsReadyCachedAsync(cancellationToken: ct).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => AiStatusButton.Content = ready ? "AI Active" : "AI Inactive");
        }).ConfigureAwait(false);
    }

    private async void OnClosedAsync(object? sender, EventArgs e)
    {
        _apiClient.Dispose();
        await _workQueue.ShutdownAsync().ConfigureAwait(false);
    }

    private void AiStatusButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 8;
    }

    public void NavigateToModelRunTab()
    {
        MainTabs.SelectedItem = ModelRunTab;
    }
}