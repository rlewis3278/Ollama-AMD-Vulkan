using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public static class LogScrollHelper
{
    public static void ScrollTextBoxToEnd(TextBox box)
    {
        if (box.Text.Length == 0)
        {
            return;
        }

        box.CaretIndex = box.Text.Length;
        box.ScrollToEnd();
    }

    public static void ScrollTextBoxToEndDeferred(TextBox box, Dispatcher dispatcher)
    {
        ScrollTextBoxToEnd(box);
        dispatcher.BeginInvoke(() => ScrollTextBoxToEnd(box), DispatcherPriority.Normal);
        dispatcher.BeginInvoke(() => ScrollTextBoxToEnd(box), DispatcherPriority.Loaded);
        dispatcher.BeginInvoke(() => ScrollTextBoxToEnd(box), DispatcherPriority.Render);
    }
}