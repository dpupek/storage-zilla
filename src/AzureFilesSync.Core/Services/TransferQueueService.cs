using System.Collections.Concurrent;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public sealed class TransferQueueService : ITransferQueueService
{
    private readonly ITransferExecutor _executor;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ConcurrentDictionary<Guid, TransferJobState> _jobs = new();
    private readonly SemaphoreSlim _workerSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _workerCount;

    public event EventHandler<TransferJobSnapshot>? JobUpdated;

    public TransferQueueService(ITransferExecutor executor, ICheckpointStore checkpointStore, int workerCount = 3)
    {
        _executor = executor;
        _checkpointStore = checkpointStore;
        _workerCount = Math.Max(1, workerCount);

        for (var i = 0; i < _workerCount; i++)
        {
            _ = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
    }

    public Guid Enqueue(TransferRequest request, bool startImmediately = true)
    {
        var jobId = Guid.NewGuid();
        var initialStatus = startImmediately ? TransferJobStatus.Queued : TransferJobStatus.Paused;
        var message = startImmediately ? null : "Queued (waiting to start)";
        var snapshot = new TransferJobSnapshot(jobId, request, initialStatus, 0, 0, message, 0);
        _jobs[jobId] = new TransferJobState(snapshot) { HoldUntilStarted = !startImmediately };
        Publish(snapshot);
        if (startImmediately)
        {
            _workerSignal.Release();
        }
        return jobId;
    }

    public async Task PauseAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Snapshot.Status == TransferJobStatus.Queued)
            {
                state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Paused, Message = "Paused" };
                Publish(state.Snapshot);
                return;
            }

            if (state.Snapshot.Status != TransferJobStatus.Running)
            {
                return;
            }

            state.PauseRequested = true;
            state.JobCancellation?.Cancel();
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public async Task ResumeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Snapshot.Status != TransferJobStatus.Paused)
            {
                return;
            }

            state.PauseRequested = false;
            state.HoldUntilStarted = false;
            state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Queued, Message = "Resumed" };
            Publish(state.Snapshot);
            _workerSignal.Release();
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public async Task RunQueuedAsync(CancellationToken cancellationToken)
    {
        var released = 0;
        foreach (var state in _jobs.Values)
        {
            await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!state.HoldUntilStarted || state.Snapshot.Status != TransferJobStatus.Paused)
                {
                    continue;
                }

                state.HoldUntilStarted = false;
                state.PauseRequested = false;
                state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Queued, Message = "Queued" };
                Publish(state.Snapshot);
                released++;
            }
            finally
            {
                state.Lock.Release();
            }
        }

        for (var i = 0; i < released; i++)
        {
            _workerSignal.Release();
        }
    }

    public async Task RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Snapshot.Status != TransferJobStatus.Failed)
            {
                return;
            }

            state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Queued, Message = null, RetryCount = state.Snapshot.RetryCount + 1 };
            Publish(state.Snapshot);
            _workerSignal.Release();
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return;
        }

        await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.PauseRequested = false;
            state.JobCancellation?.Cancel();
            state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Canceled, Message = "Canceled by user." };
            Publish(state.Snapshot);
        }
        finally
        {
            state.Lock.Release();
        }

        await _checkpointStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<TransferJobSnapshot> Snapshot() => _jobs.Values.Select(x => x.Snapshot).OrderBy(x => x.Status).ToList();

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _workerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            var next = _jobs.Values.FirstOrDefault(x => x.Snapshot.Status == TransferJobStatus.Queued);
            if (next is null)
            {
                continue;
            }

            await next.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            TransferJobSnapshot runningSnapshot;
            CancellationToken transferCancellation;
            try
            {
                if (next.Snapshot.Status != TransferJobStatus.Queued)
                {
                    continue;
                }

                next.JobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                next.PauseRequested = false;
                transferCancellation = next.JobCancellation.Token;
                var totalBytes = await _executor.EstimateSizeAsync(next.Snapshot.Request, transferCancellation).ConfigureAwait(false);
                next.Snapshot = next.Snapshot with { Status = TransferJobStatus.Running, TotalBytes = totalBytes, Message = null };
                Publish(next.Snapshot);
                runningSnapshot = next.Snapshot;
            }
            finally
            {
                next.Lock.Release();
            }

            try
            {
                var checkpoint = await _checkpointStore.LoadAsync(runningSnapshot.JobId, transferCancellation).ConfigureAwait(false);
                await _executor.ExecuteAsync(runningSnapshot.JobId, runningSnapshot.Request, checkpoint, progress =>
                {
                    var updated = runningSnapshot with
                    {
                        BytesTransferred = progress.BytesTransferred,
                        TotalBytes = progress.TotalBytes,
                        Status = TransferJobStatus.Running
                    };
                    runningSnapshot = updated;
                    next.Snapshot = updated;
                    Publish(updated);
                }, transferCancellation).ConfigureAwait(false);

                next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Completed, BytesTransferred = runningSnapshot.TotalBytes, Message = "Completed" };
                await _checkpointStore.DeleteAsync(runningSnapshot.JobId, transferCancellation).ConfigureAwait(false);
                Publish(next.Snapshot);
            }
            catch (OperationCanceledException)
            {
                await next.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (next.PauseRequested)
                    {
                        next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Paused, Message = "Paused" };
                        Publish(next.Snapshot);
                    }
                    else if (next.Snapshot.Status != TransferJobStatus.Canceled)
                    {
                        next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Canceled, Message = "Canceled" };
                        Publish(next.Snapshot);
                    }
                }
                finally
                {
                    next.Lock.Release();
                }
            }
            catch (Exception ex)
            {
                next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Failed, Message = ex.Message };
                Publish(next.Snapshot);
            }
            finally
            {
                await next.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                next.JobCancellation?.Dispose();
                next.JobCancellation = null;
                }
                finally
                {
                    next.Lock.Release();
                }
            }
        }
    }

    private void Publish(TransferJobSnapshot snapshot) => JobUpdated?.Invoke(this, snapshot);

    private sealed class TransferJobState
    {
        public TransferJobState(TransferJobSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public SemaphoreSlim Lock { get; } = new(1, 1);
        public TransferJobSnapshot Snapshot { get; set; }
        public CancellationTokenSource? JobCancellation { get; set; }
        public bool PauseRequested { get; set; }
        public bool HoldUntilStarted { get; set; }
    }
}
