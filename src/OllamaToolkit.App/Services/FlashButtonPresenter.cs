using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public sealed class FlashButtonPresenter
{
    private readonly Button _button;
    private readonly string _defaultContent;
    private readonly Brush _idleBg;
    private readonly Brush _idleBorder;
    private readonly Brush _idleForeground;
    private readonly Brush _pendingYellow;
    private readonly Brush _pendingWhite;
    private readonly Brush _activeBg;
    private readonly Brush _activeBorder;
    private readonly Brush _activeForeground;

    private readonly DispatcherTimer _flashTimer;
    private readonly DispatcherTimer _restoreTimer;
    private bool _flashPhase;
    private bool _flashing;

    public FlashButtonPresenter(Button button, Window window)
    {
        _button = button;
        _defaultContent = button.Content?.ToString() ?? string.Empty;
        _idleBg = GetBrush(window, "Brush.Button");
        _idleBorder = GetBrush(window, "Brush.PanelBorder");
        _idleForeground = GetBrush(window, "Brush.Text");
        _pendingYellow = GetBrush(window, "Brush.Warning");
        _pendingWhite = Brushes.White;
        _activeBg = GetBrush(window, "Brush.ActiveBg");
        _activeBorder = GetBrush(window, "Brush.Active");
        _activeForeground = GetBrush(window, "Brush.Text");

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _flashTimer.Tick += (_, _) => FlashTick();
        _restoreTimer = new DispatcherTimer();
        _restoreTimer.Tick += (_, _) => RestoreIdle();
    }

    public bool IsFlashing => _flashing;

    public void BeginFlash()
    {
        _restoreTimer.Stop();
        _flashing = true;
        _flashPhase = true;
        ApplyPendingFlash(true);
        _flashTimer.Start();
    }

    public void EndSuccess(string successLabel = "Refreshed", int holdSeconds = 10)
    {
        _flashTimer.Stop();
        _flashing = false;
        _button.Content = successLabel;
        _button.Background = _activeBg;
        _button.BorderBrush = _activeBorder;
        _button.Foreground = _activeForeground;

        _restoreTimer.Interval = TimeSpan.FromSeconds(holdSeconds);
        _restoreTimer.Start();
    }

    public void EndIdle()
    {
        _flashTimer.Stop();
        _restoreTimer.Stop();
        _flashing = false;
        RestoreIdle();
    }

    public void Stop()
    {
        _flashTimer.Stop();
        _restoreTimer.Stop();
        _flashing = false;
    }

    private void FlashTick()
    {
        if (!_flashing)
        {
            return;
        }

        _flashPhase = !_flashPhase;
        ApplyPendingFlash(_flashPhase);
    }

    private void ApplyPendingFlash(bool flashOn)
    {
        _button.Content = _defaultContent;
        _button.Background = flashOn ? _pendingYellow : _pendingWhite;
        _button.BorderBrush = flashOn ? _pendingYellow : _idleBorder;
        _button.Foreground = flashOn ? Brushes.Black : _idleForeground;
    }

    private void RestoreIdle()
    {
        _restoreTimer.Stop();
        _button.Content = _defaultContent;
        _button.Background = _idleBg;
        _button.BorderBrush = _idleBorder;
        _button.Foreground = _idleForeground;
    }

    private static Brush GetBrush(Window window, string key) =>
        (Brush)window.FindResource(key);
}