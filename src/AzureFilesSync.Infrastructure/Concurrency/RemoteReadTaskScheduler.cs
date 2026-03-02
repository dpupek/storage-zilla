using AzureFilesSync.Core.Contracts;

namespace AzureFilesSync.Infrastructure.Concurrency;

public sealed class RemoteReadTaskScheduler : IRemoteReadTaskScheduler
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _currentSource;
    private Task _currentExecution = Task.CompletedTask;
    private long _currentVersion;

    public Task RunLatestAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        return RunLatestAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<TResult> RunLatestAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousSource;
        Task previousExecution;
        CancellationTokenSource currentSource;
        var version = 0L;

        lock (_lock)
        {
            _currentVersion++;
            version = _currentVersion;
            previousSource = _currentSource;
            previousExecution = _currentExecution;
            currentSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentSource = currentSource;
        }

        previousSource?.Cancel();
        Task<TResult>? executionTask = null;
        executionTask = ExecuteAsync();
        lock (_lock)
        {
            _currentExecution = executionTask;
        }

        return await executionTask.ConfigureAwait(false);

        async Task<TResult> ExecuteAsync()
        {
            try
            {
                try
                {
                    await previousExecution.ConfigureAwait(false);
                }
                catch
                {
                    // Previous remote operation failures/cancellations should not block latest execution.
                }

                var result = await operation(currentSource.Token).ConfigureAwait(false);
                ThrowIfStale(version, currentSource.Token);
                return result;
            }
            finally
            {
                currentSource.Dispose();
                lock (_lock)
                {
                    if (ReferenceEquals(_currentSource, currentSource))
                    {
                        _currentSource = null;
                    }

                    if (executionTask is not null && ReferenceEquals(_currentExecution, executionTask))
                    {
                        _currentExecution = Task.CompletedTask;
                    }
                }
            }
        }
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? toCancel;
        lock (_lock)
        {
            _currentVersion++;
            toCancel = _currentSource;
            _currentSource = null;
        }

        toCancel?.Cancel();
    }

    private void ThrowIfStale(long version, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }

        lock (_lock)
        {
            if (version != _currentVersion)
            {
                throw new OperationCanceledException(token);
            }
        }
    }
}
