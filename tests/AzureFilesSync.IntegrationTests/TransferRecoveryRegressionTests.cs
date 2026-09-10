using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;
using AzureFilesSync.Infrastructure.Config;
using AzureFilesSync.Infrastructure.Transfers;

namespace AzureFilesSync.IntegrationTests;

public sealed class TransferRecoveryRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StorageZillaRecovery", Guid.NewGuid().ToString("N"));

    public TransferRecoveryRegressionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upload_ResumesOnlyRangesFromTheCurrentSource(bool sourceChanged)
    {
        // Arrange
        const int chunkSize = 64 * 1024;
        var payload = Enumerable.Range(0, chunkSize + 3).Select(i => (byte)(i % 251 + 1)).ToArray();
        var localPath = Path.Combine(_root, "upload.bin");
        await File.WriteAllBytesAsync(localPath, payload);
        var info = new FileInfo(localPath);
        var jobId = Guid.NewGuid();
        var request = new TransferRequest(TransferDirection.Upload, localPath,
            new SharePath("account", "share", "upload.bin"), TransferConflictPolicy.Overwrite,
            ChunkSizeBytes: chunkSize);
        var store = new FileCheckpointStore(Path.Combine(_root, "checkpoints"));
        var checkpoint = new TransferCheckpoint(jobId, request.Direction, localPath, request.RemotePath,
            payload.Length, chunkSize, DateTimeOffset.UtcNow,
            sourceChanged ? "previous-source" : $"{info.Length}:{info.LastWriteTimeUtc.Ticks}",
            CompletedRanges: [new TransferRangeCheckpoint(0, chunkSize)]);
        await store.SaveAsync(checkpoint, CancellationToken.None);
        using var remote = new UploadHandler(payload.Length);
        payload.AsSpan(0, chunkSize).CopyTo(remote.Bytes);
        using var client = new HttpClient(remote);
        var executor = new AzureFileTransferExecutor(new TestAuthentication(), store, new AzureClientOptions(),
            new HttpClientTransport(client));

        // Initial Assert
        Assert.Equal(0, remote.CreateCount);
        Assert.Empty(remote.UploadedOffsets);
        Assert.Equal(chunkSize, (await store.LoadAsync(jobId, CancellationToken.None))!.NextOffset);

        // Act
        await executor.ExecuteAsync(jobId, request, checkpoint, _ => { }, CancellationToken.None);

        // Assert
        Assert.Equal(payload, remote.Bytes);
        Assert.Equal(sourceChanged ? 1 : 0, remote.CreateCount);
        Assert.Equal(sourceChanged, remote.UploadedOffsets.Contains(0));
        Assert.Contains(chunkSize, remote.UploadedOffsets);
    }

    [Fact]
    public async Task DownloadCheckpoint_FlushesBufferedBytesBeforePublishingOffset()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var destination = Path.Combine(_root, "download.bin");
        var partial = ProtectedDownloadFile.GetStagingPath(destination, jobId);
        byte[] payload = [1, 2, 3];
        await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 65536, true);
        output.SetLength(payload.Length);
        await output.WriteAsync(payload);
        var checkpoint = new TransferCheckpoint(jobId, TransferDirection.Download, destination,
            new SharePath("account", "share", "file.bin"), 3, 3, DateTimeOffset.UtcNow, "etag", partial);
        var store = new ObservingCheckpointStore(() => Assert.Equal(payload, ReadShared(partial)));

        // Initial Assert
        Assert.Equal(new byte[3], ReadShared(partial));
        Assert.Null(store.Saved);

        // Act
        await ProtectedDownloadFile.SaveCheckpointAsync(output, store, checkpoint, CancellationToken.None);

        // Assert
        Assert.Same(checkpoint, store.Saved);
        Assert.Equal(payload, ReadShared(partial));
    }

    [Fact]
    public async Task DownloadCheckpoint_FailedFlushDoesNotAdvanceCheckpoint()
    {
        // Arrange
        using var output = new FailingFlushStream();
        var store = new ObservingCheckpointStore(() => { });
        var checkpoint = new TransferCheckpoint(Guid.NewGuid(), TransferDirection.Download, "file.bin",
            new SharePath("account", "share", "file.bin"), 3, 3, DateTimeOffset.UtcNow);

        // Initial Assert
        Assert.Null(store.Saved);

        // Act
        var error = await Assert.ThrowsAsync<IOException>(() =>
            ProtectedDownloadFile.SaveCheckpointAsync(output, store, checkpoint, CancellationToken.None));

        // Assert
        Assert.Equal("Flush failed", error.Message);
        Assert.Null(store.Saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_WaitsForClosedHandleAndDeletesLateCheckpointAndPartial(bool failsDuringCancellation)
    {
        // Arrange
        var destination = Path.Combine(_root, "cancel.bin");
        await File.WriteAllTextAsync(destination, "original");
        var store = new FileCheckpointStore(Path.Combine(_root, "checkpoints"));
        var executor = new StagingExecutor(store) { FailOnRelease = failsDuringCancellation };
        using var queue = new TransferQueueService(executor, store, workerCount: 1);
        var id = queue.Enqueue(DownloadRequest(destination));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var partial = ProtectedDownloadFile.GetStagingPath(destination, id);

        // Initial Assert
        Assert.True(File.Exists(partial));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));

        // Act
        var cancel = queue.CancelAsync(id, CancellationToken.None);
        Assert.False(cancel.IsCompleted);
        Assert.Equal(0, executor.CleanupCount);
        executor.Release.TrySetResult();
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(TransferJobStatus.Canceled, Assert.Single(queue.Snapshot()).Status);
        Assert.False(File.Exists(partial));
        Assert.Null(await store.LoadAsync(id, CancellationToken.None));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Equal(1, executor.CleanupCount);
    }

    [Fact]
    public async Task Pause_RetainsPartialAndCheckpointForResume()
    {
        // Arrange
        var destination = Path.Combine(_root, "pause.bin");
        var store = new FileCheckpointStore(Path.Combine(_root, "checkpoints"));
        var executor = new StagingExecutor(store);
        using var queue = new TransferQueueService(executor, store, workerCount: 1);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobUpdated += (_, snapshot) => { if (snapshot.Status == TransferJobStatus.Paused) paused.TrySetResult(); };
        var id = queue.Enqueue(DownloadRequest(destination));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Initial Assert
        Assert.True(File.Exists(ProtectedDownloadFile.GetStagingPath(destination, id)));
        Assert.Equal(TransferJobStatus.Running, Assert.Single(queue.Snapshot()).Status);

        // Act
        await queue.PauseAsync(id, CancellationToken.None);
        executor.Release.TrySetResult();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(File.Exists(ProtectedDownloadFile.GetStagingPath(destination, id)));
        Assert.NotNull(await store.LoadAsync(id, CancellationToken.None));
        Assert.Equal(0, executor.CleanupCount);
    }

    [Theory]
    [InlineData(TransferJobStatus.Canceled)]
    [InlineData(TransferJobStatus.Failed)]
    public async Task Purge_RemovesRecoveredTerminalArtifactsAndPreservesOtherJobs(TransferJobStatus status)
    {
        // Arrange
        var destination = Path.Combine(_root, "purge.bin");
        await File.WriteAllTextAsync(destination, "original");
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var request = DownloadRequest(destination);
        var partial = ProtectedDownloadFile.GetStagingPath(destination, id);
        var otherPartial = ProtectedDownloadFile.GetStagingPath(destination, otherId);
        await File.WriteAllTextAsync(partial, "abandoned");
        await File.WriteAllTextAsync(otherPartial, "retained");
        var store = new FileCheckpointStore(Path.Combine(_root, "checkpoints"));
        var jobs = new FileTransferJobStore(Path.Combine(_root, "jobs"));
        jobs.Save(new TransferJobSnapshot(id, request, status, 1, 2, null, 0));
        jobs.Save(new TransferJobSnapshot(otherId, request, TransferJobStatus.Paused, 1, 2, null, 0));
        using var queue = new TransferQueueService(new AzureFileTransferExecutor(new TestAuthentication(), store,
            new AzureClientOptions()), store, jobs, workerCount: 1);

        // Initial Assert
        Assert.Equal(2, queue.Snapshot().Count);
        Assert.True(File.Exists(partial));
        Assert.True(File.Exists(otherPartial));

        // Act
        var removed = await queue.PurgeAsync([status], CancellationToken.None);

        // Assert
        Assert.Equal(1, removed);
        Assert.Equal(otherId, Assert.Single(jobs.Load()).JobId);
        Assert.False(File.Exists(partial));
        Assert.Equal("retained", await File.ReadAllTextAsync(otherPartial));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
    }

    private static TransferRequest DownloadRequest(string path) => new(TransferDirection.Download, path,
        new SharePath("account", "share", "download.bin"), TransferConflictPolicy.Overwrite);

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private sealed class FailingFlushStream : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new IOException("Flush failed"));
    }

    private sealed class ObservingCheckpointStore(Action onSave) : ICheckpointStore
    {
        public TransferCheckpoint? Saved { get; private set; }
        public Task<TransferCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(Saved);
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            onSave();
            Saved = checkpoint;
            return Task.CompletedTask;
        }
    }

    private sealed class StagingExecutor(ICheckpointStore store) : ITransferExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CleanupCount { get; private set; }
        public bool FailOnRelease { get; init; }
        public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken) => Task.FromResult(2L);
        public async Task ExecuteAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint,
            Action<TransferProgress> progress, CancellationToken cancellationToken)
        {
            var partial = ProtectedDownloadFile.GetStagingPath(request.LocalPath, jobId);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            await output.WriteAsync(new byte[] { 1 });
            Started.TrySetResult();
            await Release.Task;
            // A range may finish persisting after the user has requested cancellation.
            await store.SaveAsync(new TransferCheckpoint(jobId, request.Direction, request.LocalPath,
                request.RemotePath, 2, 1, DateTimeOffset.UtcNow, "etag", partial), CancellationToken.None);
            progress(new TransferProgress(1, 2));
            if (FailOnRelease)
            {
                throw new IOException("Transfer failed while cancellation was pending.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        public async Task CleanupAsync(Guid jobId, TransferRequest request, CancellationToken cancellationToken)
        {
            CleanupCount++;
            await new AzureFileTransferExecutor(new TestAuthentication(), store, new AzureClientOptions())
                .CleanupAsync(jobId, request, cancellationToken);
        }
    }

    private sealed class TestAuthentication : IAuthenticationService
    {
        public TokenCredential GetCredential() => new TestCredential();
        public Task<LoginSession> SignInInteractiveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("offline-test", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class UploadHandler(int length) : HttpMessageHandler
    {
        public byte[] Bytes { get; private set; } = new byte[length];
        public List<long> UploadedOffsets { get; } = [];
        public int CreateCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(request.Method == HttpMethod.Head ? HttpStatusCode.OK : HttpStatusCode.Created)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.TryAddWithoutValidation("ETag", "\"etag\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            if (request.Method == HttpMethod.Head)
            {
                response.Content.Headers.ContentLength = Bytes.Length;
            }
            else if (request.RequestUri!.Query.Contains("comp=range"))
            {
                var range = request.Headers.GetValues("x-ms-range").Single();
                var offset = long.Parse(range.Split('=', '-')[1]);
                var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                lock (UploadedOffsets)
                {
                    UploadedOffsets.Add(offset);
                    payload.CopyTo(Bytes, (int)offset);
                }
            }
            else
            {
                CreateCount++;
                Bytes = new byte[length];
            }
            return response;
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
