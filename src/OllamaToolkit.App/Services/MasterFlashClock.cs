using System.Windows.Threading;

namespace OllamaToolkit.App.Services;

public sealed class MasterFlashClock
{
    public const int IntervalMs = 400;

    private readonly DispatcherTimer _timer;
    private int _subscriberCount;
    private bool _isAccentPhase = true;

    public MasterFlashClock(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(IntervalMs)
        };
        _timer.Tick += (_, _) =>
        {
            _isAccentPhase = !_isAccentPhase;
            PhaseChanged?.Invoke(_isAccentPhase);
        };
    }

    public bool IsAccentPhase => _isAccentPhase;

    public event Action<bool>? PhaseChanged;

    public IDisposable Subscribe(Action<bool> onPhaseChanged)
    {
        onPhaseChanged(_isAccentPhase);
        PhaseChanged += onPhaseChanged;
        _subscriberCount++;
        if (_subscriberCount == 1)
        {
            _timer.Start();
        }

        return new Subscription(this, onPhaseChanged);
    }

    private void Unsubscribe(Action<bool> onPhaseChanged)
    {
        PhaseChanged -= onPhaseChanged;
        _subscriberCount = Math.Max(0, _subscriberCount - 1);
        if (_subscriberCount == 0)
        {
            _timer.Stop();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly MasterFlashClock _clock;
        private readonly Action<bool> _handler;
        private bool _disposed;

        public Subscription(MasterFlashClock clock, Action<bool> handler)
        {
            _clock = clock;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _clock.Unsubscribe(_handler);
        }
    }
}