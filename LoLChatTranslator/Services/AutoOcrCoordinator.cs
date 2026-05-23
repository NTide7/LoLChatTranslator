namespace LoLChatTranslator.Services;

public sealed class AutoOcrCoordinator : IDisposable
{
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _generation;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _task is { IsCompleted: false };
            }
        }
    }

    public long CurrentGeneration => Interlocked.Read(ref _generation);

    public Task? CurrentTask
    {
        get
        {
            lock (_syncRoot)
            {
                return _task;
            }
        }
    }

    public AutoOcrSession Start(Func<long, CancellationToken, Task> runAsync)
    {
        ArgumentNullException.ThrowIfNull(runAsync);

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_task is { IsCompleted: false })
            {
                throw new InvalidOperationException("Automatic OCR is already running.");
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _generation);
            var token = _cts.Token;
            _task = Task.Run(() => runAsync(generation, token), CancellationToken.None);
            return new AutoOcrSession(generation, token, _task);
        }
    }

    public async Task<AutoOcrStopResult> StopAsync(TimeSpan timeout)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return new AutoOcrStopResult(null, true);
            }

            cts = _cts;
            task = _task;
            Interlocked.Increment(ref _generation);
            _cts = null;
            _task = null;
        }

        if (task is null)
        {
            cts?.Dispose();
            return new AutoOcrStopResult(null, true);
        }

        try
        {
            cts?.Cancel();
            await task.WaitAsync(timeout).ConfigureAwait(false);
            cts?.Dispose();
            return new AutoOcrStopResult(task, true);
        }
        catch (TimeoutException)
        {
            if (cts is not null)
            {
                _ = task.ContinueWith(
                    _ => cts.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return new AutoOcrStopResult(task, false);
        }
        catch (OperationCanceledException)
        {
            cts?.Dispose();
            return new AutoOcrStopResult(task, true);
        }
    }

    public bool IsCurrent(long generation)
    {
        return generation == CurrentGeneration;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _task = null;
            Interlocked.Increment(ref _generation);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AutoOcrCoordinator));
        }
    }
}

public sealed record AutoOcrSession(long Generation, CancellationToken Token, Task Task);

public sealed record AutoOcrStopResult(Task? PreviousTask, bool Completed);
