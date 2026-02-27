using AzureFilesSync.Core.Contracts;

namespace AzureFilesSync.Infrastructure.Concurrency;

public sealed class RemoteReadTaskScheduler : IRemoteReadTaskScheduler
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _currentSource;
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
        CancellationTokenSource currentSource;
        var version = 0L;

        lock (_lock)
        {
            _currentVersion++;
            version = _currentVersion;
            previousSource = _currentSource;
            currentSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentSource = currentSource;
        }

        previousSource?.Cancel();

        try
        {
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
