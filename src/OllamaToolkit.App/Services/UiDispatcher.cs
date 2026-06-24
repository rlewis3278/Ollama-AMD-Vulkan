using System.Windows;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public static class UiDispatcher
{
    public static Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return dispatcher.InvokeAsync(action).Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return dispatcher.InvokeAsync(func).Task;
    }
}