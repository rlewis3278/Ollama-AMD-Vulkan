using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OllamaToolkit.Core.Modes;

namespace OllamaToolkit.App.Services;

public sealed class ModeCardPresenter
{
    private enum ModeCardVisual
    {
        Idle,
        Active,
        Superseded,
        Pending
    }

    private static readonly Color PendingTextColor = Color.FromRgb(24, 24, 24);
    private static readonly Color SupersededBgColor = Color.FromRgb(58, 18, 18);
    private static readonly Color SupersededBorderColor = Color.FromRgb(237, 28, 36);

    private readonly Dictionary<string, Button> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TextBlock Title, TextBlock Subtitle)> _labels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModeCardVisual> _visuals = new(StringComparer.OrdinalIgnoreCase);

    private readonly Brush _idleBg;
    private readonly Brush _idleBorder;
    private readonly Brush _hoverBg;
    private readonly Brush _text;
    private readonly Brush _muted;
    private readonly Brush _activeBg;
    private readonly Brush _activeBorder;
    private readonly Brush _activeText;
    private readonly Brush _pendingYellow;
    private readonly Brush _pendingWhite;
    private readonly Brush _pendingText;
    private readonly Brush _supersededBg;
    private readonly Brush _supersededBorder;

    private readonly MasterFlashClock _clock;
    private IDisposable? _clockSubscription;
    private bool _flashPhase;
    private string? _pendingMode;
    private string? _hoveredMode;
    private bool _transitionActive;

    public ModeCardPresenter(Window window, MasterFlashClock clock)
    {
        _clock = clock;
        _idleBg = GetBrush(window, "Brush.Button");
        _idleBorder = GetBrush(window, "Brush.PanelBorder");
        _hoverBg = GetBrush(window, "Brush.Bg");
        _text = GetBrush(window, "Brush.Text");
        _muted = GetBrush(window, "Brush.Muted");
        _activeBg = GetBrush(window, "Brush.ActiveBg");
        _activeBorder = GetBrush(window, "Brush.Active");
        _activeText = GetBrush(window, "Brush.Text");
        _pendingYellow = GetBrush(window, "Brush.Warning");
        _pendingWhite = Brushes.White;
        _pendingText = new SolidColorBrush(PendingTextColor);
        _supersededBg = new SolidColorBrush(SupersededBgColor);
        _supersededBorder = new SolidColorBrush(SupersededBorderColor);

    }

    public bool IsTransitionActive => _transitionActive;

    public void Register(string modeKey, Button card, TextBlock title, TextBlock subtitle)
    {
        _cards[modeKey] = card;
        _labels[modeKey] = (title, subtitle);
        _visuals[modeKey] = ModeCardVisual.Idle;

        card.MouseEnter += (_, _) => OnCardMouseEnter(modeKey);
        card.MouseLeave += (_, _) => OnCardMouseLeave(modeKey);
    }

    public static UIElement BuildContent(string title, string subtitle, out TextBlock titleBlock, out TextBlock subtitleBlock)
    {
        titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };

        subtitleBlock = new TextBlock
        {
            Text = subtitle,
            FontSize = 14,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Height = 54,
            MaxWidth = 240
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 240,
            Children = { titleBlock, subtitleBlock }
        };
    }

    public void ApplyDefinition(ModeDefinition definition)
    {
        var key = definition.Mode.ToString();
        if (!_labels.TryGetValue(key, out var labels))
        {
            return;
        }

        labels.Title.Text = definition.ShortLabel;
        labels.Subtitle.Text = definition.CardSubtitle;
    }

    public void BeginTransition(string? previousMode, string pendingMode)
    {
        _transitionActive = true;
        _pendingMode = pendingMode;
        _flashPhase = true;
        _hoveredMode = null;

        SetVisual(pendingMode, ModeCardVisual.Pending);
        ApplyPendingFlash(_cards[pendingMode], _labels[pendingMode], flashOn: true);

        if (!string.IsNullOrWhiteSpace(previousMode)
            && !previousMode.Equals(pendingMode, StringComparison.OrdinalIgnoreCase)
            && _cards.ContainsKey(previousMode))
        {
            SetVisual(previousMode, ModeCardVisual.Superseded);
        }

        foreach (var key in _cards.Keys)
        {
            if (key.Equals(pendingMode, StringComparison.OrdinalIgnoreCase)
                || key.Equals(previousMode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SetVisual(key, ModeCardVisual.Idle);
        }

        _clockSubscription ??= _clock.Subscribe(OnPhaseChanged);
        OnPhaseChanged(_clock.IsAccentPhase);
    }

    public void EndTransition()
    {
        _clockSubscription?.Dispose();
        _clockSubscription = null;
        _transitionActive = false;
        _pendingMode = null;
    }

    public void ApplyActiveMode(string activeMode)
    {
        if (_transitionActive)
        {
            return;
        }

        foreach (var key in _cards.Keys)
        {
            SetVisual(key, key.Equals(activeMode, StringComparison.OrdinalIgnoreCase)
                ? ModeCardVisual.Active
                : ModeCardVisual.Idle);
        }
    }

    public void Stop()
    {
        _clockSubscription?.Dispose();
        _clockSubscription = null;
        _transitionActive = false;
        _pendingMode = null;
        _hoveredMode = null;
    }

    private void OnCardMouseEnter(string modeKey)
    {
        if (_transitionActive)
        {
            return;
        }

        if (!_visuals.TryGetValue(modeKey, out var visual)
            || visual is ModeCardVisual.Pending or ModeCardVisual.Superseded)
        {
            return;
        }

        _hoveredMode = modeKey;
        RenderCard(modeKey);
    }

    private void OnCardMouseLeave(string modeKey)
    {
        if (_hoveredMode?.Equals(modeKey, StringComparison.OrdinalIgnoreCase) == true)
        {
            _hoveredMode = null;
        }

        RenderCard(modeKey);
    }

    private void SetVisual(string modeKey, ModeCardVisual visual)
    {
        _visuals[modeKey] = visual;
        RenderCard(modeKey);
    }

    private void RenderCard(string modeKey)
    {
        if (!_cards.TryGetValue(modeKey, out var card) || !_labels.TryGetValue(modeKey, out var labels))
        {
            return;
        }

        if (!_transitionActive
            && _hoveredMode?.Equals(modeKey, StringComparison.OrdinalIgnoreCase) == true
            && _visuals.TryGetValue(modeKey, out var hoveredVisual)
            && hoveredVisual is ModeCardVisual.Idle or ModeCardVisual.Active)
        {
            ApplyHover(card, labels);
            return;
        }

        switch (_visuals.GetValueOrDefault(modeKey, ModeCardVisual.Idle))
        {
            case ModeCardVisual.Active:
                ApplyActive(card, labels);
                break;
            case ModeCardVisual.Superseded:
                ApplySuperseded(card, labels);
                break;
            case ModeCardVisual.Pending:
                ApplyPendingFlash(card, labels, _flashPhase);
                break;
            default:
                ApplyIdle(card, labels);
                break;
        }
    }

    private void OnPhaseChanged(bool accentPhase)
    {
        if (!_transitionActive || _pendingMode is null)
        {
            return;
        }

        _flashPhase = accentPhase;
        RenderCard(_pendingMode);
    }

    private void ApplyIdle(Button card, (TextBlock Title, TextBlock Subtitle) labels)
    {
        card.Background = _idleBg;
        card.BorderBrush = _idleBorder;
        labels.Title.Foreground = _text;
        labels.Subtitle.Foreground = _muted;
    }

    private void ApplyHover(Button card, (TextBlock Title, TextBlock Subtitle) labels)
    {
        card.Background = _hoverBg;
        card.BorderBrush = _idleBorder;
        labels.Title.Foreground = _text;
        labels.Subtitle.Foreground = _muted;
    }

    private void ApplyActive(Button card, (TextBlock Title, TextBlock Subtitle) labels)
    {
        card.Background = _activeBg;
        card.BorderBrush = _activeBorder;
        labels.Title.Foreground = _activeText;
        labels.Subtitle.Foreground = _activeText;
    }

    private void ApplySuperseded(Button card, (TextBlock Title, TextBlock Subtitle) labels)
    {
        card.Background = _supersededBg;
        card.BorderBrush = _supersededBorder;
        labels.Title.Foreground = _text;
        labels.Subtitle.Foreground = _muted;
    }

    private void ApplyPendingFlash(
        Button card,
        (TextBlock Title, TextBlock Subtitle) labels,
        bool flashOn)
    {
        card.Background = flashOn ? _pendingYellow : _pendingWhite;
        card.BorderBrush = flashOn ? _pendingYellow : _idleBorder;
        labels.Title.Foreground = _pendingText;
        labels.Subtitle.Foreground = _pendingText;
    }

    private static Brush GetBrush(Window window, string key) =>
        (Brush)window.FindResource(key);
}