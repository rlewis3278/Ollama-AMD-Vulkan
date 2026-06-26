using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

/// <summary>
/// Rate-limited ceiling pursuit for progress bars — never snaps or stalls during active tests.
/// </summary>
public sealed class SmoothProgressPresenter : IDisposable
{
    private const double MaxStepPerFrame = 0.10;
    private const double MinStepPerFrame = 0.02;
    private const double IdleDriftPerFrame = 0.025;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ProgressBar, double> _ceilings = new();
    private bool _active;

    public SmoothProgressPresenter(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current.Dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += (_, _) => OnTick();
    }

    public void SetActive(bool active)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetActive(active));
            return;
        }

        _active = active;
        if (_active)
        {
            _timer.Start();
        }
        else if (_ceilings.Count == 0)
        {
            _timer.Stop();
        }
    }

    public void SetCeiling(ProgressBar bar, double targetPercent)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetCeiling(bar, targetPercent));
            return;
        }

        targetPercent = Math.Clamp(targetPercent, 0, 100);
        if (_ceilings.TryGetValue(bar, out var existing))
        {
            targetPercent = Math.Max(existing, targetPercent);
        }
        else
        {
            targetPercent = Math.Max(bar.Value, targetPercent);
        }

        _ceilings[bar] = targetPercent;
        _timer.Start();
    }

    public void AnimateTo(ProgressBar bar, double targetPercent) => SetCeiling(bar, targetPercent);

    public void SetImmediate(ProgressBar bar, double value)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetImmediate(bar, value));
            return;
        }

        value = Math.Clamp(value, 0, 100);
        bar.Value = value;
        _ceilings[bar] = value;
        if (!_active && AllBarsAtCeiling())
        {
            _timer.Stop();
        }
    }

    public void ClearBar(ProgressBar bar)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ClearBar(bar));
            return;
        }

        _ceilings.Remove(bar);
        if (!_active && _ceilings.Count == 0)
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _ceilings.Clear();
        _active = false;
    }

    private void OnTick()
    {
        if (_ceilings.Count == 0 && !_active)
        {
            _timer.Stop();
            return;
        }

        foreach (var (bar, ceiling) in _ceilings.ToArray())
        {
            var current = bar.Value;
            if (current < ceiling - 0.001)
            {
                var delta = ceiling - current;
                var step = Math.Min(MaxStepPerFrame, Math.Max(MinStepPerFrame, delta * 0.04));
                bar.Value = Math.Min(ceiling, current + step);
                continue;
            }

            if (_active && current < 99.95)
            {
                bar.Value = Math.Min(99.95, current + IdleDriftPerFrame);
                _ceilings[bar] = Math.Max(ceiling, bar.Value);
            }
        }
    }

    private bool AllBarsAtCeiling()
    {
        foreach (var (bar, ceiling) in _ceilings)
        {
            if (bar.Value < ceiling - 0.05)
            {
                return false;
            }
        }

        return true;
    }
}