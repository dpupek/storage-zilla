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
        using var completed = new ManualResetEventSlim(false);

        queue.JobUpdated += (_, snapshot) =>
        {
            if (snapshot.JobId == jobId && snapshot.Status == TransferJobStatus.Completed)
            {
                completed.Set();
            }
        };
        #endregion

        #region Initial Assert
        Assert.NotNull(queue.Snapshot().Single(x => x.JobId == jobId));
        #endregion

        #region Act
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await queue.PauseAsync(jobId, CancellationToken.None);
        await executor.Paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await queue.ResumeAsync(jobId, CancellationToken.None);
        var signaled = completed.Wait(TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.True(signaled);
        var final = queue.Snapshot().Single(x => x.JobId == jobId);
        Assert.Equal(TransferJobStatus.Completed, final.Status);
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
}
