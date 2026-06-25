using System.Windows;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public sealed class ThrottledUpdater
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private string _pending = string.Empty;
    private Action<string>? _apply;

    public ThrottledUpdater(TimeSpan interval, Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current.Dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = interval
        };
        _timer.Tick += (_, _) =>
        {
            if (_apply is not null && _pending.Length > 0)
            {
                var batch = _pending;
                _pending = string.Empty;
                _apply(batch);
            }

            _timer.Stop();
        };
    }

    public void Append(
        string chunk,
        Action<string> apply,
        Func<string>? getCurrent = null,
        Action<string>? setCurrent = null)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Append(chunk, apply, getCurrent, setCurrent));
            return;
        }

        _apply = apply ?? (pending => setCurrent!(getCurrent!() + pending));
        _pending += chunk;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }
}