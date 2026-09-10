using System.Collections.Concurrent;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public sealed class TransferQueueService : ITransferQueueService, IDisposable
{
    private static readonly TransferJobStatus[] ActiveStatuses = [TransferJobStatus.Queued, TransferJobStatus.Running, TransferJobStatus.Paused];
    private readonly ITransferExecutor _executor;
    private readonly ICheckpointStore _checkpointStore;
    private readonly ITransferJobStore _jobStore;
    private readonly ConcurrentDictionary<Guid, TransferJobState> _jobs = new();
    private readonly SemaphoreSlim _workerSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _workerCount;
    private readonly List<Task> _workers = [];
    private readonly Lock _enqueueLock = new();
    private volatile bool _shuttingDown;
    private bool _disposed;

    public event EventHandler<TransferJobSnapshot>? JobUpdated;

    public TransferQueueService(
        ITransferExecutor executor,
        ICheckpointStore checkpointStore,
        ITransferJobStore? jobStore = null,
        int workerCount = 3)
    {
        _executor = executor;
        _checkpointStore = checkpointStore;
        _jobStore = jobStore ?? NullTransferJobStore.Instance;
        _workerCount = Math.Max(1, workerCount);

        foreach (var persisted in _jobStore.Load())
        {
            var recovered = persisted.Status is TransferJobStatus.Running or TransferJobStatus.Queued
                ? persisted with
                {
                    Status = TransferJobStatus.Paused,
                    Message = "Recovered after restart. Resume this transfer when ready."
                }
                : persisted;
            _jobs[recovered.JobId] = new TransferJobState(recovered)
            {
                HoldUntilStarted = recovered.Status == TransferJobStatus.Paused
            };
            _jobStore.Save(recovered);
        }

        for (var i = 0; i < _workerCount; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_cts.Token)));
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

        Task workerCompletion;
        await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.CancelRequested = true;
            state.PauseRequested = false;
            state.JobCancellation?.Cancel();
            workerCompletion = state.WorkerCompletion?.Task ?? Task.CompletedTask;
            state.Snapshot = state.Snapshot with { Status = TransferJobStatus.Canceled, Message = "Canceled by user." };
            Publish(state.Snapshot);
        }
        finally
        {
            state.Lock.Release();
        }

        // Once cancellation is accepted, finish cleanup even if the caller goes away.
        await workerCompletion.ConfigureAwait(false);
        await state.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DiscardArtifactsAsync(state.Snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public async Task<int> PurgeAsync(IReadOnlyCollection<TransferJobStatus> statuses, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (statuses.Count == 0)
        {
            return 0;
        }

        var statusSet = statuses.ToHashSet();
        var removed = 0;
        foreach (var pair in _jobs.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = pair.Value;
            if (!statusSet.Contains(state.Snapshot.Status) || ActiveStatuses.Contains(state.Snapshot.Status))
            {
                continue;
            }
            await (state.WorkerCompletion?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken).ConfigureAwait(false);
            await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = state.Snapshot;
                if (!statusSet.Contains(snapshot.Status) || ActiveStatuses.Contains(snapshot.Status))
                {
                    continue;
                }
                await DiscardArtifactsAsync(snapshot, cancellationToken).ConfigureAwait(false);
                _jobStore.Delete(pair.Key);
                if (_jobs.TryRemove(pair.Key, out _))
                {
                    removed++;
                }
            }
            finally
            {
                state.Lock.Release();
            }
        }

        return removed;
    }

    public IReadOnlyList<TransferJobSnapshot> Snapshot() => _jobs.Values.Select(x => x.Snapshot).OrderBy(x => x.Status).ToList();

    private async Task DiscardArtifactsAsync(TransferJobSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _executor.CleanupAsync(snapshot.JobId, snapshot.Request, cancellationToken).ConfigureAwait(false);
        await _checkpointStore.DeleteAsync(snapshot.JobId, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_enqueueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shuttingDown = true;
            _cts.Cancel();
        }

        var stopped = false;
        try
        {
            stopped = Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            stopped = true;
        }
        finally
        {
            if (stopped)
            {
                _cts.Dispose();
                _workerSignal.Dispose();
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _workerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!TryClaimNextQueued(out var next))
            {
                continue;
            }

            TransferJobSnapshot runningSnapshot;
            CancellationToken transferCancellation;
            var claimedLockReleased = false;
            try
            {
                next.JobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                next.CancelRequested = false;
                next.PauseRequested = false;
                transferCancellation = next.JobCancellation.Token;
                next.Snapshot = next.Snapshot with { Status = TransferJobStatus.Running, Message = null };
                Publish(next.Snapshot);
                runningSnapshot = next.Snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                next.JobCancellation?.Dispose();
                next.JobCancellation = null;
                claimedLockReleased = true;
                next.WorkerCompletion?.TrySetResult();
                next.Lock.Release();
                return;
            }
            catch (OperationCanceledException)
            {
                next.Snapshot = next.Snapshot with { Status = TransferJobStatus.Canceled, Message = "Canceled" };
                Publish(next.Snapshot);
                next.JobCancellation?.Dispose();
                next.JobCancellation = null;
                claimedLockReleased = true;
                next.WorkerCompletion?.TrySetResult();
                next.Lock.Release();
                continue;
            }
            catch (Exception ex)
            {
                var isUnresolvedAskConflict = ex.Message.Contains("Ask policy was unresolved", StringComparison.OrdinalIgnoreCase);
                next.Snapshot = next.Snapshot with
                {
                    Status = isUnresolvedAskConflict ? TransferJobStatus.Canceled : TransferJobStatus.Failed,
                    Message = ex.Message
                };
                Publish(next.Snapshot);
                next.JobCancellation?.Dispose();
                next.JobCancellation = null;
                claimedLockReleased = true;
                next.WorkerCompletion?.TrySetResult();
                next.Lock.Release();
                continue;
            }
            finally
            {
                if (!claimedLockReleased)
                {
                    next.Lock.Release();
                    claimedLockReleased = true;
                }
            }

            try
            {
                var totalBytes = await _executor.EstimateSizeAsync(runningSnapshot.Request, transferCancellation).ConfigureAwait(false);
                transferCancellation.ThrowIfCancellationRequested();
                runningSnapshot = runningSnapshot with { TotalBytes = totalBytes };
                next.Snapshot = runningSnapshot;
                Publish(runningSnapshot);

                var checkpoint = await _checkpointStore.LoadAsync(runningSnapshot.JobId, transferCancellation).ConfigureAwait(false);
                await _executor.ExecuteAsync(runningSnapshot.JobId, runningSnapshot.Request, checkpoint, progress =>
                {
                    if (next.CancelRequested)
                    {
                        return;
                    }
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

                transferCancellation.ThrowIfCancellationRequested();
                next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Completed, BytesTransferred = runningSnapshot.TotalBytes, Message = "Completed" };
                await _checkpointStore.DeleteAsync(runningSnapshot.JobId, transferCancellation).ConfigureAwait(false);
                Publish(next.Snapshot);
            }
            catch (OperationCanceledException)
            {
                await next.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (next.CancelRequested)
                    {
                        next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Canceled, Message = "Canceled by user." };
                        Publish(next.Snapshot);
                    }
                    else if (_shuttingDown)
                    {
                        next.Snapshot = runningSnapshot with { Status = TransferJobStatus.Paused, Message = "Paused during application shutdown." };
                        Publish(next.Snapshot);
                    }
                    else if (next.PauseRequested)
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
                    Status = next.CancelRequested || isUnresolvedAskConflict ? TransferJobStatus.Canceled : TransferJobStatus.Failed,
                    Message = next.CancelRequested ? "Canceled by user." : ex.Message
                };
                Publish(next.Snapshot);
            }
            finally
            {
                await next.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    next.JobCancellation?.Dispose();
                    next.JobCancellation = null;
                }
                finally
                {
                    next.WorkerCompletion?.TrySetResult();
                    if (next.Snapshot.Status == TransferJobStatus.Queued)
                    {
                        _workerSignal.Release();
                    }
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

            if (state.Snapshot.Status == TransferJobStatus.Queued && state.WorkerCompletion?.Task.IsCompleted != false)
            {
                state.WorkerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                next = state;
                return true;
            }

            state.Lock.Release();
        }

        next = null!;
        return false;
    }

    private void Publish(TransferJobSnapshot snapshot)
    {
        _jobStore.Save(snapshot);
        JobUpdated?.Invoke(this, snapshot);
    }

    private static string BuildTransferKey(TransferRequest request)
    {
        var localPath = NormalizeLocalPath(request.LocalPath);
        var account = (request.RemotePath.StorageAccountName ?? string.Empty).Trim().ToLowerInvariant();
        var share = (request.RemotePath.ShareName ?? string.Empty).Trim().ToLowerInvariant();
        var provider = request.RemotePath.ProviderKind.ToString().ToLowerInvariant();
        var remoteRelative = request.RemotePath.NormalizeRelativePath().ToLowerInvariant();
        return $"{request.Direction}|{localPath}|{account}|{share}|{provider}|{remoteRelative}";
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
        public TaskCompletionSource? WorkerCompletion { get; set; }
        public volatile bool CancelRequested;
        public bool PauseRequested { get; set; }
        public bool HoldUntilStarted { get; set; }
    }

    private sealed class NullTransferJobStore : ITransferJobStore
    {
        public static NullTransferJobStore Instance { get; } = new();

        public IReadOnlyList<TransferJobSnapshot> Load() => [];
        public void Save(TransferJobSnapshot snapshot) { }
        public void Delete(Guid jobId) { }
    }
}
