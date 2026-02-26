using System.Collections.Concurrent;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public sealed class TransferQueueService : ITransferQueueService
{
    private static readonly TransferJobStatus[] ActiveStatuses = [TransferJobStatus.Queued, TransferJobStatus.Running, TransferJobStatus.Paused];
    private readonly ITransferExecutor _executor;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ConcurrentDictionary<Guid, TransferJobState> _jobs = new();
    private readonly SemaphoreSlim _workerSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _workerCount;
    private readonly Lock _enqueueLock = new();

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

    public EnqueueResult EnqueueOrGetExisting(TransferRequest request, bool startImmediately = true)
    {
        lock (_enqueueLock)
        {
            var transferKey = BuildTransferKey(request);
            var existing = _jobs.Values.FirstOrDefault(state =>
                ActiveStatuses.Contains(state.Snapshot.Status) &&
                string.Equals(BuildTransferKey(state.Snapshot.Request), transferKey, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                return new EnqueueResult(existing.Snapshot.JobId, AddedNew: false, existing.Snapshot.Status);
            }

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

            return new EnqueueResult(jobId, AddedNew: true, initialStatus);
        }
    }

    public Guid Enqueue(TransferRequest request, bool startImmediately = true)
    {
        return EnqueueOrGetExisting(request, startImmediately).JobId;
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
                if (state.Snapshot.Status == TransferJobStatus.Paused)
                {
                    state.HoldUntilStarted = false;
                    state.PauseRequested = false;
                    state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Queued, Message = "Queued" };
                    Publish(state.Snapshot);
                    released++;
                    continue;
                }

                if (state.Snapshot.Status == TransferJobStatus.Queued)
                {
                    released++;
                }
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

    public async Task PauseAllAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _jobs.Values)
        {
            await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state.Snapshot.Status == TransferJobStatus.Queued)
                {
                    state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Paused, Message = "Paused" };
                    Publish(state.Snapshot);
                    continue;
                }

                if (state.Snapshot.Status != TransferJobStatus.Running)
                {
                    continue;
                }

                state.PauseRequested = true;
                state.JobCancellation?.Cancel();
            }
            finally
            {
                state.Lock.Release();
            }
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
            if (!TryClaimNextQueued(out var next))
            {
                continue;
            }

            TransferJobSnapshot runningSnapshot;
            CancellationToken transferCancellation;
            try
            {
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
                var isUnresolvedAskConflict = ex.Message.Contains("Ask policy was unresolved", StringComparison.OrdinalIgnoreCase);
                next.Snapshot = runningSnapshot with
                {
                    Status = isUnresolvedAskConflict ? TransferJobStatus.Canceled : TransferJobStatus.Failed,
                    Message = ex.Message
                };
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

    private bool TryClaimNextQueued(out TransferJobState next)
    {
        foreach (var state in _jobs.Values)
        {
            if (!state.Lock.Wait(0))
            {
                continue;
            }

            if (state.Snapshot.Status == TransferJobStatus.Queued)
            {
                next = state;
                return true;
            }

            state.Lock.Release();
        }

        next = null!;
        return false;
    }

    private void Publish(TransferJobSnapshot snapshot) => JobUpdated?.Invoke(this, snapshot);

    private static string BuildTransferKey(TransferRequest request)
    {
        var localPath = NormalizeLocalPath(request.LocalPath);
        var account = (request.RemotePath.StorageAccountName ?? string.Empty).Trim().ToLowerInvariant();
        var share = (request.RemotePath.ShareName ?? string.Empty).Trim().ToLowerInvariant();
        var remoteRelative = request.RemotePath.NormalizeRelativePath().ToLowerInvariant();
        return $"{request.Direction}|{localPath}|{account}|{share}|{remoteRelative}";
    }

    private static string NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('/', '\\');
        return normalized.TrimEnd('\\').ToLowerInvariant();
    }

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
