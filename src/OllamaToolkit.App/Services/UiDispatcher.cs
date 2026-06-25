using System.Windows;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public static class UiDispatcher
{
    private static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static Task InvokeAsync(Action action) =>
        Dispatcher.InvokeAsync(action).Task;

    public static Task InvokeAsync(Func<Task> action) =>
        Dispatcher.InvokeAsync(action).Task.Unwrap();

    public static Task<T> InvokeAsync<T>(Func<T> func) =>
        Dispatcher.InvokeAsync(func).Task;
}