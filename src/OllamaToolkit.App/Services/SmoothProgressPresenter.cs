using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

/// <summary>
/// Lerps <see cref="ProgressBar.Value"/> toward targets on the UI thread for smooth, monotonic updates.
/// </summary>
public sealed class SmoothProgressPresenter : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ProgressBar, double> _targets = new();

    public SmoothProgressPresenter(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current.Dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += (_, _) => OnTick();
    }

    public void AnimateTo(ProgressBar bar, double targetPercent)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AnimateTo(bar, targetPercent));
            return;
        }

        targetPercent = Math.Clamp(targetPercent, 0, 100);
        var current = bar.Value;
        if (targetPercent < current - 0.05 && targetPercent < 99.5)
        {
            targetPercent = current;
        }

        if (Math.Abs(targetPercent - current) < 0.05)
        {
            bar.Value = targetPercent;
            _targets.Remove(bar);
            if (_targets.Count == 0)
            {
                _timer.Stop();
            }

            return;
        }

        _targets[bar] = targetPercent;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void SetImmediate(ProgressBar bar, double value)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetImmediate(bar, value));
            return;
        }

        value = Math.Clamp(value, 0, 100);
        bar.Value = value;
        _targets.Remove(bar);
        if (_targets.Count == 0)
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _targets.Clear();
    }

    private void OnTick()
    {
        if (_targets.Count == 0)
        {
            _timer.Stop();
            return;
        }

        foreach (var (bar, target) in _targets.ToArray())
        {
            var current = bar.Value;
            if (Math.Abs(target - current) < 0.15)
            {
                bar.Value = target;
                _targets.Remove(bar);
                continue;
            }

            var delta = target - current;
            var step = Math.Max(0.25, Math.Abs(delta) * 0.18);
            bar.Value = delta > 0 ? Math.Min(target, current + step) : Math.Max(target, current - step);
        }

        if (_targets.Count == 0)
        {
            _timer.Stop();
        }
    }
}