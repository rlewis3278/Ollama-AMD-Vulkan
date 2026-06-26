namespace OllamaToolkit.Core.Ollama;

public sealed class InferenceActivityCallbacks
{
    public Action? OnStart { get; init; }
    public Action? OnEnd { get; init; }

    public Scope Begin() => new(this);

    public sealed class Scope : IDisposable
    {
        private readonly InferenceActivityCallbacks? _callbacks;
        private bool _disposed;

        internal Scope(InferenceActivityCallbacks? callbacks)
        {
            _callbacks = callbacks;
            _callbacks?.OnStart?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callbacks?.OnEnd?.Invoke();
        }
    }
}