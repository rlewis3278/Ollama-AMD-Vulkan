namespace OllamaToolkit.App.Services;

public sealed class AiActivityHub
{
    private readonly object _lock = new();
    private int _toolkitDepth;
    private int _ollamaDepth;

    public event Action<bool>? ToolkitActivityChanged;
    public event Action<bool>? OllamaActivityChanged;

    public bool IsToolkitActive
    {
        get
        {
            lock (_lock)
            {
                return _toolkitDepth > 0;
            }
        }
    }

    public bool IsOllamaActive
    {
        get
        {
            lock (_lock)
            {
                return _ollamaDepth > 0;
            }
        }
    }

    public void EnterToolkitAi() => ChangeDepth(ref _toolkitDepth, +1, ToolkitActivityChanged);

    public void ExitToolkitAi() => ChangeDepth(ref _toolkitDepth, -1, ToolkitActivityChanged);

    public void EnterOllamaInference() => ChangeDepth(ref _ollamaDepth, +1, OllamaActivityChanged);

    public void ExitOllamaInference() => ChangeDepth(ref _ollamaDepth, -1, OllamaActivityChanged);

    private void ChangeDepth(ref int depth, int delta, Action<bool>? changed)
    {
        bool? becameActive = null;
        lock (_lock)
        {
            var before = depth;
            depth = Math.Max(0, depth + delta);
            if (before == 0 && depth > 0)
            {
                becameActive = true;
            }
            else if (before > 0 && depth == 0)
            {
                becameActive = false;
            }
        }

        if (becameActive is not null)
        {
            changed?.Invoke(becameActive.Value);
        }
    }
}

public sealed class ToolkitAiActivityScope : IDisposable
{
    private readonly AiActivityHub _hub;
    private bool _disposed;

    public ToolkitAiActivityScope(AiActivityHub hub)
    {
        _hub = hub;
        _hub.EnterToolkitAi();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.ExitToolkitAi();
    }
}