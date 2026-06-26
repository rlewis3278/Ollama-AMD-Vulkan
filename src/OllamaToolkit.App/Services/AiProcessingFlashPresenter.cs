using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public sealed class AiProcessingFlashPresenter
{
    private readonly Button _button;
    private readonly Window _window;
    private readonly MasterFlashClock _clock;
    private readonly Brush _pendingYellow;
    private readonly Brush _pendingBlack;
    private ControlTemplate? _savedTemplate;
    private IDisposable? _clockSubscription;
    private bool _flashing;
    private string _idleContent = string.Empty;
    private string _activeContent = string.Empty;
    private Brush _idleBg = Brushes.Transparent;
    private Brush _idleBorder = Brushes.Transparent;
    private Brush _idleForeground = Brushes.White;

    public AiProcessingFlashPresenter(Button button, Window window, MasterFlashClock clock)
    {
        _button = button;
        _window = window;
        _clock = clock;
        _pendingYellow = GetBrush(window, "Brush.Warning");
        _pendingBlack = GetBrush(window, "Brush.Bg");
    }

    public void SetPresentation(
        string idleContent,
        string activeContent,
        Brush background,
        Brush border,
        Brush foreground)
    {
        _idleContent = idleContent;
        _activeContent = string.IsNullOrWhiteSpace(activeContent) ? idleContent : activeContent;
        _idleBg = background;
        _idleBorder = border;
        _idleForeground = foreground;

        if (!_flashing)
        {
            ApplyIdle();
        }
    }

    public void SetIdlePresentation(string content, Brush background, Brush border, Brush foreground) =>
        SetPresentation(content, content, background, border, foreground);

    public void SetActive(bool active)
    {
        if (active)
        {
            BeginProcessingFlash();
        }
        else
        {
            EndProcessingFlash();
        }
    }

    public void BeginProcessingFlash()
    {
        _flashing = true;
        UseFlashTemplate();
        _clockSubscription ??= _clock.Subscribe(OnPhaseChanged);
        OnPhaseChanged(_clock.IsAccentPhase);
        _button.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => OnPhaseChanged(_clock.IsAccentPhase));
    }

    public void EndProcessingFlash()
    {
        _clockSubscription?.Dispose();
        _clockSubscription = null;
        _flashing = false;
        RestoreDefaultTemplate();
        ApplyIdle();
    }

    public void Stop() => EndProcessingFlash();

    private void OnPhaseChanged(bool accentPhase)
    {
        if (!_flashing)
        {
            return;
        }

        ApplyFlash(accentPhase);
    }

    private void ApplyFlash(bool flashOn)
    {
        _button.Content = _activeContent;
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