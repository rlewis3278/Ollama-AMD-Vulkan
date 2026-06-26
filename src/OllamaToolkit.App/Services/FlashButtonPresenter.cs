using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public enum FlashSuccessStyle
{
    Active,
    Info
}

public enum FlashColorScheme
{
    YellowBlack,
    PurpleBlack
}

public sealed class FlashButtonPresenter
{
    private readonly Button _button;
    private readonly Window _window;
    private readonly MasterFlashClock _clock;
    private readonly string _defaultContent;
    private readonly Brush _idleBg;
    private readonly Brush _idleBorder;
    private readonly Brush _idleForeground;
    private readonly Brush _pendingYellow;
    private readonly Brush _pendingPurple;
    private readonly Brush _pendingBlack;
    private FlashColorScheme _colorScheme = FlashColorScheme.YellowBlack;
    private readonly Brush _activeBg;
    private readonly Brush _activeBorder;
    private readonly Brush _activeForeground;
    private readonly Brush _infoBg;
    private readonly Brush _infoBorder;
    private readonly Brush _infoForeground;

    private readonly DispatcherTimer _restoreTimer;
    private ControlTemplate? _savedTemplate;
    private IDisposable? _clockSubscription;
    private bool _flashing;
    private Action? _onRestored;

    public FlashButtonPresenter(Button button, Window window, MasterFlashClock clock)
    {
        _button = button;
        _window = window;
        _clock = clock;
        _defaultContent = button.Content?.ToString() ?? string.Empty;
        _idleBg = GetBrush(window, "Brush.Button");
        _idleBorder = GetBrush(window, "Brush.PanelBorder");
        _idleForeground = GetBrush(window, "Brush.Text");
        _pendingYellow = GetBrush(window, "Brush.Warning");
        _pendingPurple = GetBrush(window, "Brush.Purple");
        _pendingBlack = GetBrush(window, "Brush.Bg");
        _activeBg = GetBrush(window, "Brush.ActiveBg");
        _activeBorder = GetBrush(window, "Brush.Active");
        _activeForeground = GetBrush(window, "Brush.Text");
        _infoBg = GetBrush(window, "Brush.InfoBg");
        _infoBorder = GetBrush(window, "Brush.Info");
        _infoForeground = GetBrush(window, "Brush.Info");

        _restoreTimer = new DispatcherTimer();
        _restoreTimer.Tick += (_, _) => RestoreIdle();
    }

    public bool IsFlashing => _flashing;

    public void BeginFlash(FlashColorScheme scheme = FlashColorScheme.YellowBlack)
    {
        _colorScheme = scheme;
        _restoreTimer.Stop();
        _flashing = true;
        UseFlashTemplate();
        _clockSubscription ??= _clock.Subscribe(OnPhaseChanged);
        OnPhaseChanged(_clock.IsAccentPhase);
        _button.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => OnPhaseChanged(_clock.IsAccentPhase));
    }

    public void EndSuccess(
        string successLabel = "Refreshed",
        int holdSeconds = 10,
        Action? onRestored = null,
        FlashSuccessStyle style = FlashSuccessStyle.Active)
    {
        StopClockSubscription();
        _flashing = false;
        RestoreDefaultTemplate();
        _onRestored = onRestored;
        _button.Content = successLabel;

        if (style == FlashSuccessStyle.Info)
        {
            _button.Background = _infoBg;
            _button.BorderBrush = _infoBorder;
            _button.Foreground = _infoForeground;
        }
        else
        {
            _button.Background = _activeBg;
            _button.BorderBrush = _activeBorder;
            _button.Foreground = _activeForeground;
        }

        _restoreTimer.Interval = TimeSpan.FromSeconds(holdSeconds);
        _restoreTimer.Start();
    }

    public void EndIdle()
    {
        StopClockSubscription();
        _restoreTimer.Stop();
        _flashing = false;
        _onRestored = null;
        RestoreDefaultTemplate();
        RestoreIdle();
    }

    public void Stop()
    {
        StopClockSubscription();
        _restoreTimer.Stop();
        _flashing = false;
        _onRestored = null;
        RestoreDefaultTemplate();
    }

    private void OnPhaseChanged(bool accentPhase)
    {
        if (!_flashing)
        {
            return;
        }

        ApplyPendingFlash(accentPhase);
    }

    private void StopClockSubscription()
    {
        _clockSubscription?.Dispose();
        _clockSubscription = null;
    }

    private void ApplyPendingFlash(bool flashOn)
    {
        var accent = _colorScheme == FlashColorScheme.PurpleBlack ? _pendingPurple : _pendingYellow;
        _button.Content = _defaultContent;
        _button.Background = flashOn ? accent : _pendingBlack;
        _button.BorderBrush = flashOn ? accent : _pendingBlack;
        _button.Foreground = flashOn ? Brushes.Black : Brushes.White;
    }

    private void RestoreIdle()
    {
        _restoreTimer.Stop();
        RestoreDefaultTemplate();
        _button.Content = _defaultContent;
        _button.Background = _idleBg;
        _button.BorderBrush = _idleBorder;
        _button.Foreground = _idleForeground;

        var callback = _onRestored;
        _onRestored = null;
        callback?.Invoke();
    }

    private void UseFlashTemplate()
    {
        if (_savedTemplate is null)
        {
            _savedTemplate = _button.Template;
        }

        _button.Template = (ControlTemplate)_window.FindResource("ToolkitFlashButtonTemplate");
    }

    private void RestoreDefaultTemplate()
    {
        if (_savedTemplate is not null)
        {
            _button.Template = _savedTemplate;
            _savedTemplate = null;
        }
    }

    private static Brush GetBrush(Window window, string key) =>
        (Brush)window.FindResource(key);
}