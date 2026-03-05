using System.Reflection;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.RemoteEditing;

namespace AzureFilesSync.IntegrationTests;

public sealed class RemoteEditSessionServiceTests
{
    [Fact]
    public async Task OpenAsync_DownloadsAndLaunchesLocalFile()
    {
        #region Arrange
        var transfer = new StubTransferExecutor();
        var browser = new StubAzureFilesBrowserService();
        var localOps = new StubLocalFileOperationsService();
        var service = new RemoteEditSessionService(transfer, browser, localOps);
        var remotePath = new SharePath("storage", "share", "notes.txt");
        browser.CurrentEntry = new RemoteEntry("notes.txt", "notes.txt", false, 5, DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        Assert.Empty(localOps.OpenedPaths);
        #endregion

        #region Act
        var opened = await service.OpenAsync(remotePath, "notes.txt", CancellationToken.None);
        #endregion

        #region Assert
        Assert.Single(localOps.OpenedPaths);
        Assert.Equal(opened.LocalPath, localOps.OpenedPaths[0]);
        Assert.True(File.Exists(opened.LocalPath));
        await service.DiscardAsync(opened.SessionId, CancellationToken.None);
        service.Dispose();
        #endregion
    }

    [Fact]
    public async Task OpenAsync_WhenLocalOpenFails_CleansUpTempFile()
    {
        #region Arrange
        var transfer = new StubTransferExecutor();
        var browser = new StubAzureFilesBrowserService();
        var localOps = new StubLocalFileOperationsService { ThrowOnOpen = true };
        var service = new RemoteEditSessionService(transfer, browser, localOps);
        var remotePath = new SharePath("storage", "share", "broken-open-test.txt");
        browser.CurrentEntry = new RemoteEntry("broken-open-test.txt", "broken-open-test.txt", false, 5, DateTimeOffset.UtcNow);
        var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureFilesSync", "remote-edit", "storage", "share");
        var before = Directory.Exists(tempRoot)
            ? Directory.GetFiles(tempRoot, "*_broken-open-test.txt", SearchOption.TopDirectoryOnly)
            : [];
        #endregion

        #region Initial Assert
        Assert.Empty(localOps.OpenedPaths);
        #endregion

        #region Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync(remotePath, "broken-open-test.txt", CancellationToken.None));
        #endregion

        #region Assert
        var after = Directory.Exists(tempRoot)
            ? Directory.GetFiles(tempRoot, "*_broken-open-test.txt", SearchOption.TopDirectoryOnly)
            : [];
        Assert.Equal(before.Length, after.Length);
        service.Dispose();
        #endregion
    }

    [Fact]
    public async Task GetPendingChangesAsync_DirtyHintWithoutFingerprintDelta_DoesNotReportPending()
    {
        #region Arrange
        var transfer = new StubTransferExecutor();
        var browser = new StubAzureFilesBrowserService();
        var localOps = new StubLocalFileOperationsService();
        var service = new RemoteEditSessionService(transfer, browser, localOps);
        var remotePath = new SharePath("storage", "share", "no-change.txt");
        browser.CurrentEntry = new RemoteEntry("no-change.txt", "no-change.txt", false, 5, DateTimeOffset.UtcNow);
        var opened = await service.OpenAsync(remotePath, "no-change.txt", CancellationToken.None);

        var markDirty = typeof(RemoteEditSessionService)
            .GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.NonPublic)!;
        markDirty.Invoke(service, [opened.SessionId]);
        #endregion

        #region Initial Assert
        Assert.True(File.Exists(opened.LocalPath));
        #endregion

        #region Act
        var pending = await service.GetPendingChangesAsync(CancellationToken.None);
        var syncResult = await service.SyncAsync(opened.SessionId, overwriteIfRemoteChanged: false, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Empty(pending);
        Assert.Equal(RemoteEditSyncOutcome.NoLocalChanges, syncResult.Outcome);
        Assert.Empty(transfer.UploadedRequests);
        await service.DiscardAsync(opened.SessionId, CancellationToken.None);
        service.Dispose();
        #endregion
    }

    [Fact]
    public async Task SyncAsync_WhenRemoteChangedAndOverwriteFalse_ReturnsConfirmationOutcome()
    {
        #region Arrange
        var transfer = new StubTransferExecutor();
        var browser = new StubAzureFilesBrowserService();
        var localOps = new StubLocalFileOperationsService();
        var service = new RemoteEditSessionService(transfer, browser, localOps);
        var remotePath = new SharePath("storage", "share", "notes.txt");
        browser.CurrentEntry = new RemoteEntry("notes.txt", "notes.txt", false, 5, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
        var opened = await service.OpenAsync(remotePath, "notes.txt", CancellationToken.None);

        await File.WriteAllTextAsync(opened.LocalPath, "updated-local", CancellationToken.None);
        browser.CurrentEntry = new RemoteEntry("notes.txt", "notes.txt", false, 6, new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        #endregion

        #region Initial Assert
        Assert.True(File.Exists(opened.LocalPath));
        #endregion

        #region Act
        var result = await service.SyncAsync(opened.SessionId, overwriteIfRemoteChanged: false, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(RemoteEditSyncOutcome.RemoteChangedNeedsConfirmation, result.Outcome);
        Assert.Empty(transfer.UploadedRequests);
        await service.DiscardAsync(opened.SessionId, CancellationToken.None);
        service.Dispose();
        #endregion
    }
    [Fact]
    public async Task SyncAsync_WhenLocalFileIsLocked_ReturnsLocalFileInUseAndKeepsSession()
    {
        #region Arrange
        var transfer = new StubTransferExecutor { ThrowOnUploadIOException = true };
        var browser = new StubAzureFilesBrowserService();
        var localOps = new StubLocalFileOperationsService();
        var service = new RemoteEditSessionService(transfer, browser, localOps);
        var remotePath = new SharePath("storage", "share", "locked.doc");
        browser.CurrentEntry = new RemoteEntry("locked.doc", "locked.doc", false, 10, DateTimeOffset.UtcNow);
        var opened = await service.OpenAsync(remotePath, "locked.doc", CancellationToken.None);

        await File.WriteAllTextAsync(opened.LocalPath, "updated-local", CancellationToken.None);
        #endregion

        #region Initial Assert
        Assert.True(File.Exists(opened.LocalPath));
        #endregion

        #region Act
        var result = await service.SyncAsync(opened.SessionId, overwriteIfRemoteChanged: false, CancellationToken.None);
        var pending = await service.GetPendingChangesAsync(CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(RemoteEditSyncOutcome.LocalFileInUse, result.Outcome);
        Assert.Single(pending);
        await service.DiscardAsync(opened.SessionId, CancellationToken.None);
        service.Dispose();
        #endregion
    }

    private sealed class StubTransferExecutor : ITransferExecutor
    {
        public List<TransferRequest> UploadedRequests { get; } = [];
        public bool ThrowOnUploadIOException { get; set; }

        public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task ExecuteAsync(
            Guid jobId,
            TransferRequest request,
            TransferCheckpoint? checkpoint,
            Action<TransferProgress> progress,
            CancellationToken cancellationToken)
        {
            if (request.Direction == TransferDirection.Download)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.LocalPath)!);
                File.WriteAllText(request.LocalPath, "seed");
                return Task.CompletedTask;
            }

            if (ThrowOnUploadIOException)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
            }

            UploadedRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAzureFilesBrowserService : IAzureFilesBrowserService
    {
        public RemoteEntry? CurrentEntry { get; set; }

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([]);

        public Task<RemoteDirectoryPage> ListDirectoryPageAsync(SharePath path, string? continuationToken, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteDirectoryPage([], null, false));

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult(CurrentEntry);
    }

    private sealed class StubLocalFileOperationsService : ILocalFileOperationsService
    {
        public List<string> OpenedPaths { get; } = [];
        public bool ThrowOnOpen { get; set; }

        public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OpenAsync(string path, CancellationToken cancellationToken)
        {
            if (ThrowOnOpen)
            {
                throw new InvalidOperationException("Failed to launch associated editor.");
            }

            OpenedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task OpenWithAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}


