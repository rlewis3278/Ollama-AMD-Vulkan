using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OllamaToolkit.Core.Modes;

namespace OllamaToolkit.App.Services;

public sealed class ModeCardPresenter
{
    private static readonly Color PendingTextColor = Color.FromRgb(24, 24, 24);
    private static readonly Color SupersededBgColor = Color.FromRgb(58, 18, 18);
    private static readonly Color SupersededBorderColor = Color.FromRgb(237, 28, 36);

    private readonly Dictionary<string, Button> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TextBlock Title, TextBlock Subtitle)> _labels =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Brush _idleBg;
    private readonly Brush _idleBorder;
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

    private readonly DispatcherTimer _flashTimer;
    private bool _flashPhase;
    private string? _pendingMode;
    private bool _transitionActive;

    public ModeCardPresenter(Window window)
    {
        _idleBg = GetBrush(window, "Brush.Button");
        _idleBorder = GetBrush(window, "Brush.PanelBorder");
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

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _flashTimer.Tick += (_, _) => FlashTick();
    }

    public bool IsTransitionActive => _transitionActive;

    public void Register(string modeKey, Button card, TextBlock title, TextBlock subtitle)
    {
        _cards[modeKey] = card;
        _labels[modeKey] = (title, subtitle);
    }

    public static UIElement BuildContent(string title, string subtitle, out TextBlock titleBlock, out TextBlock subtitleBlock)
    {
        titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };

        subtitleBlock = new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            LineHeight = 14,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Height = 42,
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

        foreach (var key in _cards.Keys)
        {
            _cards[key].IsEnabled = false;
        }

        if (_cards.TryGetValue(pendingMode, out var pendingCard))
        {
            pendingCard.IsEnabled = true;
            ApplyPendingFlash(pendingCard, _labels[pendingMode], flashOn: true);
        }

        if (!string.IsNullOrWhiteSpace(previousMode)
            && !previousMode.Equals(pendingMode, StringComparison.OrdinalIgnoreCase)
            && _cards.TryGetValue(previousMode, out var previousCard))
        {
            ApplySuperseded(previousCard, _labels[previousMode]);
        }

        foreach (var (key, card) in _cards)
        {
            if (key.Equals(pendingMode, StringComparison.OrdinalIgnoreCase)
                || key.Equals(previousMode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ApplyIdle(card, _labels[key]);
        }

        _flashTimer.Start();
    }

    public void EndTransition()
    {
        _flashTimer.Stop();
        _transitionActive = false;
        _pendingMode = null;

        foreach (var card in _cards.Values)
        {
            card.IsEnabled = true;
        }
    }

    public void ApplyActiveMode(string activeMode)
    {
        if (_transitionActive)
        {
            return;
        }

        foreach (var (key, card) in _cards)
        {
            if (key.Equals(activeMode, StringComparison.OrdinalIgnoreCase))
            {
                ApplyActive(card, _labels[key]);
            }
            else
            {
                ApplyIdle(card, _labels[key]);
            }
        }
    }

    public void Stop()
    {
        _flashTimer.Stop();
        _transitionActive = false;
        _pendingMode = null;
    }

    private void FlashTick()
    {
        if (!_transitionActive || _pendingMode is null || !_cards.TryGetValue(_pendingMode, out var card))
        {
            return;
        }

        _flashPhase = !_flashPhase;
        ApplyPendingFlash(card, _labels[_pendingMode], _flashPhase);
    }

    private void ApplyIdle(Button card, (TextBlock Title, TextBlock Subtitle) labels)
    {
        card.Background = _idleBg;
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