using System.Threading.Channels;

namespace OllamaToolkit.App.Services;

public sealed class BackgroundWorkQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>();

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly ActivityLogService? _activityLog;

    public BackgroundWorkQueue(ActivityLogService? activityLog = null)
    {
        _activityLog = activityLog;
        _worker = Task.Run(ProcessAsync);
    }

    public ValueTask EnqueueAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(work, cancellationToken);

    public async Task ShutdownAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts.Dispose();
    }

    private async Task ProcessAsync()
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            try
            {
                await work(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _activityLog?.Write("Error", $"Background job failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"BackgroundWorkQueue: {ex}");
            }
        }
    }
}