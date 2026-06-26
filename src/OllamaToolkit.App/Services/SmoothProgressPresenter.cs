using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

/// <summary>
/// Rate-limited ceiling pursuit — only the overall bar and active mode bar animate during a test run.
/// </summary>
public sealed class SmoothProgressPresenter : IDisposable
{
    private const double MaxStepPerFrame = 0.10;
    private const double MinStepPerFrame = 0.02;
    private const double IdleDriftPerFrame = 0.025;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ProgressBar, double> _ceilings = new();
    private readonly HashSet<ProgressBar> _frozenBars = new();
    private ProgressBar? _overallBar;
    private ProgressBar? _activeModeBar;
    private bool _active;
    private bool _halted;

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
        if (_active && !_halted)
        {
            _timer.Start();
        }
        else if (!_active && AllBarsAtCeiling())
        {
            _timer.Stop();
        }
    }

    public void SetOverallBar(ProgressBar bar) => _overallBar = bar;

    public void SetActiveModeBar(ProgressBar? bar)
    {
        if (bar is not null)
        {
            _frozenBars.Remove(bar);
        }

        _activeModeBar = bar;
    }

    public void ResetModeBars()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(ResetModeBars);
            return;
        }

        _activeModeBar = null;
        _frozenBars.Clear();
    }

    public void FreezeBar(ProgressBar bar, double value)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => FreezeBar(bar, value));
            return;
        }

        value = Math.Clamp(value, 0, 100);
        bar.Value = value;
        _ceilings[bar] = value;
        _frozenBars.Add(bar);
        if (ReferenceEquals(bar, _activeModeBar))
        {
            _activeModeBar = null;
        }
    }

    public void Halt()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Halt);
            return;
        }

        _halted = true;
        _active = false;
        _activeModeBar = null;
        _timer.Stop();
        foreach (var bar in _ceilings.Keys.ToArray())
        {
            _frozenBars.Add(bar);
        }
    }

    public void BeginSession()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(BeginSession);
            return;
        }

        _halted = false;
        _frozenBars.Clear();
        _activeModeBar = null;
    }

    public void SetCeiling(ProgressBar bar, double targetPercent)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetCeiling(bar, targetPercent));
            return;
        }

        if (_halted || _frozenBars.Contains(bar))
        {
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
        if (!_halted)
        {
            _timer.Start();
        }
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
        _frozenBars.Remove(bar);
        if (!_active && !_halted && AllBarsAtCeiling())
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
        _frozenBars.Remove(bar);
        if (ReferenceEquals(bar, _activeModeBar))
        {
            _activeModeBar = null;
        }

        if (ReferenceEquals(bar, _overallBar))
        {
            _overallBar = null;
        }

        if (!_active && _ceilings.Count == 0)
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _ceilings.Clear();
        _frozenBars.Clear();
        _active = false;
        _halted = true;
    }

    private void OnTick()
    {
        if (_halted || (_ceilings.Count == 0 && !_active))
        {
            _timer.Stop();
            return;
        }

        foreach (var (bar, ceiling) in _ceilings.ToArray())
        {
            if (_frozenBars.Contains(bar))
            {
                continue;
            }

            var current = bar.Value;
            if (current < ceiling - 0.001)
            {
                var delta = ceiling - current;
                var step = Math.Min(MaxStepPerFrame, Math.Max(MinStepPerFrame, delta * 0.04));
                bar.Value = Math.Min(ceiling, current + step);
                continue;
            }

            if (!_active || current >= 99.95)
            {
                continue;
            }

            if (!ReferenceEquals(bar, _overallBar) && !ReferenceEquals(bar, _activeModeBar))
            {
                continue;
            }

            bar.Value = Math.Min(99.95, current + IdleDriftPerFrame);
            _ceilings[bar] = Math.Max(ceiling, bar.Value);
        }
    }

    private bool AllBarsAtCeiling()
    {
        foreach (var (bar, ceiling) in _ceilings)
        {
            if (_frozenBars.Contains(bar))
            {
                continue;
            }

            if (bar.Value < ceiling - 0.05)
            {
                return false;
            }
        }

        return true;
    }
}