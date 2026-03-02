using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using System.Threading;

namespace AzureFilesSync.Infrastructure.Concurrency;

public sealed class RemoteOperationCoordinator : IRemoteOperationCoordinator
{
    private readonly IRemoteReadTaskScheduler _scheduler;
    private long _sequence;
    private int _lastCancelReason = (int)RemoteOperationCancelReason.Unknown;

    public RemoteOperationCoordinator(IRemoteReadTaskScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public RemoteOperationCancelReason LastCancelReason => (RemoteOperationCancelReason)Volatile.Read(ref _lastCancelReason);

    public Task RunLatestAsync(
        RemoteOperationType operationType,
        Func<RemoteOperationScope, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        return RunLatestAsync(
            operationType,
            async (scope, token) =>
            {
                await operation(scope, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<TResult> RunLatestAsync<TResult>(
        RemoteOperationType operationType,
        Func<RemoteOperationScope, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var scope = CreateScope(operationType, isUserInitiated: true);
        Volatile.Write(ref _lastCancelReason, (int)RemoteOperationCancelReason.Unknown);

        try
        {
            return await _scheduler.RunLatestAsync(
                token => operation(scope, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && LastCancelReason == RemoteOperationCancelReason.Unknown)
            {
                Volatile.Write(ref _lastCancelReason, (int)RemoteOperationCancelReason.ReplacedByLatest);
            }

            throw;
        }
    }

    public void CancelCurrent(RemoteOperationCancelReason reason = RemoteOperationCancelReason.UserRequested)
    {
        Volatile.Write(ref _lastCancelReason, (int)reason);
        _scheduler.CancelCurrent();
    }

    private RemoteOperationScope CreateScope(RemoteOperationType operationType, bool isUserInitiated)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return new RemoteOperationScope(
            operationType,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            sequence,
            isUserInitiated);
    }
}
