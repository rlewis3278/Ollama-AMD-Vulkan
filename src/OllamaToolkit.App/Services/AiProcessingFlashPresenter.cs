using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public sealed class AiProcessingFlashPresenter
{
    private readonly Button _button;
    private readonly Brush _pendingYellow;
    private readonly Brush _pendingBlack;
    private readonly DispatcherTimer _flashTimer;
    private bool _flashPhase;
    private bool _flashing;
    private string _idleContent = string.Empty;
    private Brush _idleBg = Brushes.Transparent;
    private Brush _idleBorder = Brushes.Transparent;
    private Brush _idleForeground = Brushes.White;

    public AiProcessingFlashPresenter(Button button, Window window)
    {
        _button = button;
        _pendingYellow = GetBrush(window, "Brush.Warning");
        _pendingBlack = GetBrush(window, "Brush.Bg");
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _flashTimer.Tick += (_, _) =>
        {
            if (!_flashing)
            {
                return;
            }

            _flashPhase = !_flashPhase;
            ApplyFlash(_flashPhase);
        };
    }

    public void SetIdlePresentation(string content, Brush background, Brush border, Brush foreground)
    {
        _idleContent = content;
        _idleBg = background;
        _idleBorder = border;
        _idleForeground = foreground;

        if (!_flashing)
        {
            ApplyIdle();
        }
    }

    public void BeginProcessingFlash()
    {
        if (_flashing)
        {
            return;
        }

        _flashing = true;
        _flashPhase = true;
        ApplyFlash(true);
        _flashTimer.Start();
    }

    public void EndProcessingFlash()
    {
        _flashTimer.Stop();
        _flashing = false;
        ApplyIdle();
    }

    public void Stop() => EndProcessingFlash();

    private void ApplyFlash(bool flashOn)
    {
        _button.Content = _idleContent;
        _button.Background = flashOn ? _pendingYellow : _pendingBlack;
        _button.BorderBrush = flashOn ? _pendingYellow : _pendingBlack;
        _button.Foreground = flashOn ? Brushes.Black : Brushes.White;
    }

    private void ApplyIdle()
    {
        _button.Content = _idleContent;
        _button.Background = _idleBg;
        _button.BorderBrush = _idleBorder;
        _button.Foreground = _idleForeground;
    }

    private static Brush GetBrush(Window window, string key) =>
        (Brush)window.FindResource(key);
}