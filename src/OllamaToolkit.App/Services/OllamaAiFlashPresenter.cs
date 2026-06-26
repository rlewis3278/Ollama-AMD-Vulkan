using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OllamaToolkit.App.Services;

public sealed class OllamaAiFlashPresenter
{
    private readonly AiProcessingFlashPresenter _inner;

    public OllamaAiFlashPresenter(Button button, Window window) =>
        _inner = new AiProcessingFlashPresenter(button, window);

    public void SetPresentation(
        string idleContent,
        string activeContent,
        Brush background,
        Brush border,
        Brush foreground) =>
        _inner.SetPresentation(idleContent, activeContent, background, border, foreground);

    public void SetIdlePresentation(string content, Brush background, Brush border, Brush foreground) =>
        _inner.SetIdlePresentation(content, background, border, foreground);

    public void SetActive(bool active) => _inner.SetActive(active);

    public void Stop() => _inner.Stop();
}