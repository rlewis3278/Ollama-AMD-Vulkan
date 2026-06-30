using System.Threading.Channels;

namespace OllamaToolkit.App.Services;

public enum WorkQueuePriority
{
    Normal,
    AllowDuringTests
}

public sealed class BackgroundWorkQueue
{
    private const int DefaultWorkerCount = 3;

    private readonly Channel<QueuedWork> _channel = Channel.CreateUnbounded<QueuedWork>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private readonly ActivityLogService? _activityLog;
    private readonly ToolkitDiagnosticsService? _diagnostics;
    private int _jobSequence;
    private int _testSuiteLock;

    public BackgroundWorkQueue(
        ActivityLogService? activityLog = null,
        ToolkitDiagnosticsService? diagnostics = null,
        int workerCount = DefaultWorkerCount)
    {
        _activityLog = activityLog;
        _diagnostics = diagnostics;
        var count = Math.Clamp(workerCount, 1, 8);
        _workers = Enumerable.Range(0, count)
            .Select(_ => Task.Run(ProcessAsync))
            .ToArray();
    }

    public int WorkerCount => _workers.Length;

    public bool IsTestSuiteLocked => Volatile.Read(ref _testSuiteLock) > 0;

    public bool TryEnterTestSuiteLock() =>
        Interlocked.CompareExchange(ref _testSuiteLock, 1, 0) == 0;

    public void ExitTestSuiteLock() => Interlocked.Exchange(ref _testSuiteLock, 0);

    public ValueTask EnqueueAsync(
        Func<CancellationToken, Task> work,
        WorkQueuePriority priority = WorkQueuePriority.Normal,
        CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new QueuedWork(work, priority), cancellationToken);

    public async Task ShutdownAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts.Dispose();
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            if (item.Priority == WorkQueuePriority.Normal && IsTestSuiteLocked)
            {
                _diagnostics?.Write("WorkQueue", "Job deferred — test suite lock active");
                while (IsTestSuiteLocked && !_cts.IsCancellationRequested)
                {
                    await Task.Delay(250, _cts.Token).ConfigureAwait(false);
                }
            }

            var jobId = Interlocked.Increment(ref _jobSequence);
            _diagnostics?.Write("WorkQueue", $"Job #{jobId} started");
            try
            {
                await item.Work(_cts.Token).ConfigureAwait(false);
                _diagnostics?.Write("WorkQueue", $"Job #{jobId} completed");
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                _diagnostics?.Write("WorkQueue", $"Job #{jobId} cancelled (shutdown)");
                break;
            }
            catch (Exception ex)
            {
                _activityLog?.Write("Error", $"Background job failed: {ex.Message}");
                _diagnostics?.Write("WorkQueue", $"Job #{jobId} failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"BackgroundWorkQueue: {ex}");
            }
        }
    }

    private sealed record QueuedWork(Func<CancellationToken, Task> Work, WorkQueuePriority Priority);
}