using System.Windows;
using System.Windows.Controls;

namespace OllamaToolkit.App.Services;

public sealed class FlashButtonRegistry
{
    private readonly Window _window;
    private readonly MasterFlashClock _clock;
    private readonly Dictionary<Button, FlashButtonPresenter> _presenters = new();

    public FlashButtonRegistry(Window window, MasterFlashClock clock)
    {
        _window = window;
        _clock = clock;
    }

    public FlashButtonPresenter For(Button button)
    {
        if (!_presenters.TryGetValue(button, out var presenter))
        {
            presenter = new FlashButtonPresenter(button, _window, _clock);
            _presenters[button] = presenter;
        }

        return presenter;
    }

    public bool TryBegin(Button button, FlashColorScheme scheme = FlashColorScheme.YellowBlack)
    {
        var presenter = For(button);
        if (presenter.IsFlashing)
        {
            return false;
        }

        presenter.BeginFlash(scheme);
        return true;
    }

    public void EndSuccess(
        Button button,
        string successLabel = "Done",
        int holdSeconds = 10,
        Action? onRestored = null,
        FlashSuccessStyle style = FlashSuccessStyle.Active) =>
        For(button).EndSuccess(successLabel, holdSeconds, onRestored, style);

    public void EndIdle(Button button) => For(button).EndIdle();

    public void StopAll()
    {
        foreach (var presenter in _presenters.Values)
        {
            presenter.Stop();
        }
    }
}