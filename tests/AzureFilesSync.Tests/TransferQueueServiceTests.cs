using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;

namespace AzureFilesSync.Tests;

public sealed class TransferQueueServiceTests
{
    [Fact]
    public async Task Enqueue_ProcessesJob_ToCompleted()
    {
        #region Arrange
        var executor = new StubTransferExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        TransferJobSnapshot? latest = null;
        Guid jobId = Guid.Empty;
        using var completed = new ManualResetEventSlim(false);

        queue.JobUpdated += (_, snapshot) =>
        {
            if (jobId != Guid.Empty && snapshot.JobId == jobId)
            {
                latest = snapshot;
                if (snapshot.Status is TransferJobStatus.Completed or TransferJobStatus.Failed)
                {
                    completed.Set();
                }
            }
        };

        var request = new TransferRequest(TransferDirection.Upload, "C:/tmp/file.txt", new SharePath("acct", "share", "file.txt"));
        jobId = queue.Enqueue(request);
        #endregion

        #region Initial Assert
        Assert.NotNull(queue.Snapshot().Single(x => x.JobId == jobId));
        #endregion

        #region Act
        var signaled = completed.Wait(TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.True(signaled);
        Assert.NotNull(latest);
        Assert.Equal(TransferJobStatus.Completed, latest!.Status);
        Assert.Equal(100, latest.TotalBytes);
        Assert.Equal(100, latest.BytesTransferred);
        #endregion
    }

    [Fact]
    public async Task PauseThenResume_RunningJob_CompletesSuccessfully()
    {
        #region Arrange
        var executor = new BlockingTransferExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        var request = new TransferRequest(TransferDirection.Upload, "C:/tmp/file.txt", new SharePath("acct", "share", "file.txt"));
        var jobId = queue.Enqueue(request);
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.JobUpdated += (_, snapshot) =>
        {
            if (snapshot.JobId != jobId)
            {
                return;
            }

            if (snapshot.Status == TransferJobStatus.Paused)
            {
                paused.TrySetResult(true);
            }

            if (snapshot.Status == TransferJobStatus.Completed)
            {
                completed.TrySetResult(true);
            }
        };
        #endregion

        #region Initial Assert
        Assert.NotNull(queue.Snapshot().Single(x => x.JobId == jobId));
        #endregion

        #region Act
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.PauseAsync(jobId, CancellationToken.None);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.ResumeAsync(jobId, CancellationToken.None);
        var signaled = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        #endregion

        #region Assert
        Assert.True(signaled);
        var final = queue.Snapshot().Single(x => x.JobId == jobId);
        Assert.Equal(TransferJobStatus.Completed, final.Status);
        #endregion
    }

    [Fact]
    public async Task PauseAll_PausesRunningJob()
    {
        #region Arrange
        var executor = new BlockingTransferExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        var request = new TransferRequest(TransferDirection.Upload, "C:/tmp/file.txt", new SharePath("acct", "share", "file.txt"));
        var jobId = queue.Enqueue(request);
        using var paused = new ManualResetEventSlim(false);
        queue.JobUpdated += (_, snapshot) =>
        {
            if (snapshot.JobId == jobId && snapshot.Status == TransferJobStatus.Paused)
            {
                paused.Set();
            }
        };
        #endregion

        #region Initial Assert
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        #endregion

        #region Act
        await queue.PauseAllAsync(CancellationToken.None);
        var signaled = paused.Wait(TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.True(signaled);
        var final = queue.Snapshot().Single(x => x.JobId == jobId);
        Assert.Equal(TransferJobStatus.Paused, final.Status);
        #endregion
    }

    [Fact]
    public async Task RunQueued_SingleRun_DrainsAllPausedJobs()
    {
        #region Arrange
        var executor = new StubTransferExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 3);
        var jobIds = new List<Guid>();
        using var completed = new ManualResetEventSlim(false);

        queue.JobUpdated += (_, snapshot) =>
        {
            if (jobIds.Contains(snapshot.JobId) && snapshot.Status == TransferJobStatus.Completed)
            {
                var done = queue.Snapshot().Count(x => jobIds.Contains(x.JobId) && x.Status == TransferJobStatus.Completed);
                if (done == jobIds.Count)
                {
                    completed.Set();
                }
            }
        };

        for (var i = 0; i < 6; i++)
        {
            var request = new TransferRequest(TransferDirection.Download, $@"C:\tmp\file-{i}.txt", new SharePath("acct", "share", $"file-{i}.txt"));
            jobIds.Add(queue.Enqueue(request, startImmediately: false));
        }
        #endregion

        #region Initial Assert
        Assert.Equal(6, queue.Snapshot().Count(x => x.Status == TransferJobStatus.Paused));
        #endregion

        #region Act
        await queue.RunQueuedAsync(CancellationToken.None);
        var signaled = completed.Wait(TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.True(signaled);
        Assert.Equal(6, queue.Snapshot().Count(x => x.Status == TransferJobStatus.Completed));
        #endregion
    }

    [Fact]
    public void EnqueueOrGetExisting_DuplicateActiveTransfer_DoesNotAddSecondJob()
    {
        #region Arrange
        var executor = new StubTransferExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        var request = new TransferRequest(TransferDirection.Upload, @"C:\tmp\file.txt", new SharePath("acct", "share", "file.txt"));
        #endregion

        #region Initial Assert
        Assert.Empty(queue.Snapshot());
        #endregion

        #region Act
        var first = queue.EnqueueOrGetExisting(request, startImmediately: false);
        var second = queue.EnqueueOrGetExisting(request, startImmediately: true);
        #endregion

        #region Assert
        Assert.True(first.AddedNew);
        Assert.False(second.AddedNew);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Single(queue.Snapshot());
        #endregion
    }

    [Fact]
    public async Task EnqueueOrGetExisting_AfterCompleted_AllowsNewJob()
    {
        #region Arrange
        var executor = new StubTransferExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        var request = new TransferRequest(TransferDirection.Upload, @"C:\tmp\file.txt", new SharePath("acct", "share", "file.txt"));
        TransferJobSnapshot? latest = null;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobUpdated += (_, snapshot) =>
        {
            if (snapshot.Status == TransferJobStatus.Completed)
            {
                latest = snapshot;
                done.TrySetResult(true);
            }
        };
        #endregion

        #region Initial Assert
        Assert.Empty(queue.Snapshot());
        #endregion

        #region Act
        var first = queue.EnqueueOrGetExisting(request);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.EnqueueOrGetExisting(request, startImmediately: false);
        #endregion

        #region Assert
        Assert.NotNull(latest);
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.True(second.AddedNew);
        Assert.Equal(2, queue.Snapshot().Count);
        #endregion
    }

    [Fact]
    public async Task Execute_UnresolvedAskConflict_MapsToCanceled()
    {
        #region Arrange
        const string unresolvedAskMessage = "Transfer canceled: conflict requires user decision, but Ask policy was unresolved at queue time.";
        var executor = new ThrowingTransferExecutor(unresolvedAskMessage);
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        var request = new TransferRequest(
            TransferDirection.Upload,
            @"C:\tmp\file.txt",
            new SharePath("acct", "share", "file.txt"),
            ConflictPolicy: TransferConflictPolicy.Ask);
        var jobId = queue.Enqueue(request);
        using var completed = new ManualResetEventSlim(false);
        queue.JobUpdated += (_, snapshot) =>
        {
            if (snapshot.JobId == jobId && snapshot.Status == TransferJobStatus.Canceled)
            {
                completed.Set();
            }
        };
        #endregion

        #region Initial Assert
        Assert.NotNull(queue.Snapshot().Single(x => x.JobId == jobId));
        #endregion

        #region Act
        var signaled = completed.Wait(TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.True(signaled);
        var final = queue.Snapshot().Single(x => x.JobId == jobId);
        Assert.Equal(TransferJobStatus.Canceled, final.Status);
        Assert.Contains("Ask policy was unresolved", final.Message, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task EstimateFailure_FailsJob_AndWorkerContinuesWithNextJob()
    {
        #region Arrange
        var executor = new EstimateFailureExecutor();
        var checkpoints = new InMemoryCheckpointStore();
        var queue = new TransferQueueService(executor, checkpoints, workerCount: 1);
        var failedRequest = new TransferRequest(TransferDirection.Upload, @"C:\tmp\fail.txt", new SharePath("acct", "share", "fail.txt"));
        var successfulRequest = new TransferRequest(TransferDirection.Upload, @"C:\tmp\ok.txt", new SharePath("acct", "share", "ok.txt"));
        var failedId = queue.Enqueue(failedRequest);
        var successId = queue.Enqueue(successfulRequest);
        using var completed = new ManualResetEventSlim(false);

        queue.JobUpdated += (_, snapshot) =>
        {
            if (snapshot.JobId == successId && snapshot.Status == TransferJobStatus.Completed)
            {
                completed.Set();
            }
        };
        #endregion

        #region Initial Assert
        Assert.Equal(2, queue.Snapshot().Count);
        #endregion

        #region Act
        var signaled = completed.Wait(TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.True(signaled);
        var failed = queue.Snapshot().Single(x => x.JobId == failedId);
        var succeeded = queue.Snapshot().Single(x => x.JobId == successId);
        Assert.Equal(TransferJobStatus.Failed, failed.Status);
        Assert.Equal(TransferJobStatus.Completed, succeeded.Status);
        #endregion
    }

    private sealed class StubTransferExecutor : ITransferExecutor
    {
        public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken) => Task.FromResult(100L);

        public async Task ExecuteAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            progress(new TransferProgress(50, 100));
            await Task.Delay(20, cancellationToken);
            progress(new TransferProgress(100, 100));
        }
    }

    private sealed class BlockingTransferExecutor : ITransferExecutor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken) => Task.FromResult(100L);

        public async Task ExecuteAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
        {
            _attempts++;
            Started.TrySetResult(true);

            if (_attempts == 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Paused.TrySetResult(true);
                    throw;
                }
            }

            progress(new TransferProgress(100, 100));
        }
    }

    private sealed class InMemoryCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<Guid, TransferCheckpoint> _checkpoints = [];

        public Task<TransferCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
        {
            _checkpoints.TryGetValue(jobId, out var checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            _checkpoints[checkpoint.JobId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
        {
            _checkpoints.Remove(jobId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingTransferExecutor : ITransferExecutor
    {
        private readonly string _message;

        public ThrowingTransferExecutor(string message)
        {
            _message = message;
        }

        public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken) => Task.FromResult(100L);

        public Task ExecuteAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(_message);
        }
    }

    private sealed class EstimateFailureExecutor : ITransferExecutor
    {
        public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            if (request.LocalPath.Contains("fail", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Estimate failed.");
            }

            return Task.FromResult(100L);
        }

        public Task ExecuteAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
        {
            progress(new TransferProgress(100, 100));
            return Task.CompletedTask;
        }
    }
}
