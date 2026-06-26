using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OllamaToolkit.App.Services;

public sealed class StartupSplashPresenter
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);

    private readonly UIElement _overlay;
    private readonly UIElement _mainContent;
    private readonly TextBlock _revisionText;

    public StartupSplashPresenter(
        UIElement overlay,
        UIElement mainContent,
        TextBlock revisionText)
    {
        _overlay = overlay;
        _mainContent = mainContent;
        _revisionText = revisionText;
    }

    public Task ShowAndFadeInAsync()
    {
        _revisionText.Text = AppBuildInfo.GetDisplayRevision();
        _mainContent.IsEnabled = false;
        _overlay.Visibility = Visibility.Visible;
        _overlay.Opacity = 0;
        _overlay.IsHitTestVisible = true;
        return AnimateOpacityAsync(0, 1);
    }

    public async Task HideAndFadeOutAsync()
    {
        await AnimateOpacityAsync(_overlay.Opacity, 0).ConfigureAwait(true);
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.IsHitTestVisible = false;
        _mainContent.IsEnabled = true;
    }

    private Task AnimateOpacityAsync(double from, double to)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(FadeDuration),
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => completion.TrySetResult();
        _overlay.BeginAnimation(UIElement.OpacityProperty, animation);
        return completion.Task;
    }
}