using System.Windows;
using System.Windows.Controls;

namespace OllamaToolkit.App.Services;

public sealed class FlashButtonRegistry
{
    private readonly Window _window;
    private readonly Dictionary<Button, FlashButtonPresenter> _presenters = new();

    public FlashButtonRegistry(Window window) => _window = window;

    public FlashButtonPresenter For(Button button)
    {
        if (!_presenters.TryGetValue(button, out var presenter))
        {
            presenter = new FlashButtonPresenter(button, _window);
            _presenters[button] = presenter;
        }

        return presenter;
    }

    public bool TryBegin(Button button)
    {
        var presenter = For(button);
        if (presenter.IsFlashing)
        {
            return false;
        }

        presenter.BeginFlash();
        return true;
    }

    public void EndSuccess(Button button, string successLabel = "Done", int holdSeconds = 10) =>
        For(button).EndSuccess(successLabel, holdSeconds);

    public void EndIdle(Button button) => For(button).EndIdle();

    public void StopAll()
    {
        foreach (var presenter in _presenters.Values)
        {
            presenter.Stop();
        }
    }
}