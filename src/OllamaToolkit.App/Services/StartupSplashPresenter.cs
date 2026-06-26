using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OllamaToolkit.App.Services;

public sealed class StartupSplashPresenter
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromSeconds(5);

    private readonly UIElement _overlay;
    private readonly UIElement _mainContent;
    private readonly TextBlock _revisionText;
    private DateTime? _shownAtUtc;

    public StartupSplashPresenter(
        UIElement overlay,
        UIElement mainContent,
        TextBlock revisionText)
    {
        _overlay = overlay;
        _mainContent = mainContent;
        _revisionText = revisionText;
    }

    public async Task ShowAndFadeInAsync()
    {
        _revisionText.Text = AppBuildInfo.GetDisplayRevision();
        _mainContent.IsEnabled = false;
        _overlay.Visibility = Visibility.Visible;
        _overlay.Opacity = 0;
        _overlay.IsHitTestVisible = true;

        await AnimateOpacityAsync(0, 1).ConfigureAwait(true);
        PinOpacity(1);
        _shownAtUtc = DateTime.UtcNow;
    }

    public async Task HideAndFadeOutAsync()
    {
        await EnsureMinimumVisibleTimeAsync(MinimumVisibleDuration).ConfigureAwait(true);
        PinOpacity(1);
        await AnimateOpacityAsync(1, 0).ConfigureAwait(true);
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.IsHitTestVisible = false;
        _mainContent.IsEnabled = true;
        _shownAtUtc = null;
    }

    private async Task EnsureMinimumVisibleTimeAsync(TimeSpan minimum)
    {
        if (_shownAtUtc is null)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _shownAtUtc.Value;
        var remaining = minimum - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining).ConfigureAwait(true);
        }
    }

    private void PinOpacity(double opacity)
    {
        _overlay.BeginAnimation(UIElement.OpacityProperty, null);
        _overlay.Opacity = opacity;
    }

    private Task AnimateOpacityAsync(double from, double to)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(FadeDuration),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) => completion.TrySetResult();
        _overlay.BeginAnimation(UIElement.OpacityProperty, animation);
        return completion.Task;
    }
}