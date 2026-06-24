using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public sealed class ThrottledUpdater
{
    private readonly DispatcherTimer _timer;
    private string _pending = string.Empty;
    private Action<string>? _apply;

    public ThrottledUpdater(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) =>
        {
            if (_apply is not null && _pending.Length > 0)
            {
                _apply(_pending);
                _pending = string.Empty;
            }

            _timer.Stop();
        };
    }

    public void Append(string chunk, Action<string> apply, Func<string> getCurrent, Action<string> setCurrent)
    {
        _apply = value =>
        {
            setCurrent(getCurrent() + value);
        };

        _pending += chunk;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }
}