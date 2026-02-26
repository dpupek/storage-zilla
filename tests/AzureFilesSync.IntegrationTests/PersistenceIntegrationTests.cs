using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Local;
using AzureFilesSync.Infrastructure.Transfers;

namespace AzureFilesSync.IntegrationTests;

public sealed class PersistenceIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public PersistenceIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AzureFilesSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task ConnectionProfileStore_RoundTrips_ProfileData()
    {
        #region Arrange
        var store = new FileConnectionProfileStore(_tempRoot);
        var expected = new ConnectionProfile(
            "sub-1",
            "storage-1",
            "share-1",
            @"C:\work",
            "project/src",
            true,
            6,
            5242880,
            TransferConflictPolicy.Ask,
            TransferConflictPolicy.Overwrite,
            [@"C:\work", @"D:\dev"],
            ["project/src", "project/bin"]);
        #endregion

        #region Initial Assert
        var initial = await store.LoadAsync(CancellationToken.None);
        Assert.False(initial.IncludeDeletes);
        #endregion

        #region Act
        await store.SaveAsync(expected, CancellationToken.None);
        var actual = await store.LoadAsync(CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(expected.SubscriptionId, actual.SubscriptionId);
        Assert.Equal(expected.StorageAccountName, actual.StorageAccountName);
        Assert.Equal(expected.FileShareName, actual.FileShareName);
        Assert.Equal(expected.LocalPath, actual.LocalPath);
        Assert.Equal(expected.RemotePath, actual.RemotePath);
        Assert.Equal(expected.IncludeDeletes, actual.IncludeDeletes);
        Assert.Equal(expected.TransferMaxConcurrency, actual.TransferMaxConcurrency);
        Assert.Equal(expected.TransferMaxBytesPerSecond, actual.TransferMaxBytesPerSecond);
        Assert.Equal(expected.UploadConflictDefaultPolicy, actual.UploadConflictDefaultPolicy);
        Assert.Equal(expected.DownloadConflictDefaultPolicy, actual.DownloadConflictDefaultPolicy);
        Assert.Equal(expected.RecentLocalPaths, actual.RecentLocalPaths);
        Assert.Equal(expected.RecentRemotePaths, actual.RecentRemotePaths);
        #endregion
    }

    [Fact]
    public async Task CheckpointStore_SaveLoadDelete_WorksAcrossCycles()
    {
        #region Arrange
        var checkpointRoot = Path.Combine(_tempRoot, "checkpoints");
        var store = new FileCheckpointStore(checkpointRoot);
        var jobId = Guid.NewGuid();
        var expected = new TransferCheckpoint(
            jobId,
            TransferDirection.Upload,
            @"C:\work\file.txt",
            new SharePath("storage-1", "share-1", "file.txt"),
            1024,
            512,
            DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        var missing = await store.LoadAsync(jobId, CancellationToken.None);
        Assert.Null(missing);
        #endregion

        #region Act
        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(jobId, CancellationToken.None);
        await store.DeleteAsync(jobId, CancellationToken.None);
        var afterDelete = await store.LoadAsync(jobId, CancellationToken.None);
        #endregion

        #region Assert
        Assert.NotNull(loaded);
        Assert.Equal(expected.JobId, loaded!.JobId);
        Assert.Equal(expected.TotalBytes, loaded.TotalBytes);
        Assert.Equal(expected.NextOffset, loaded.NextOffset);
        Assert.Null(afterDelete);
        #endregion
    }

    [Fact]
    public async Task LocalBrowserService_ListsFilesAndDirectories()
    {
        #region Arrange
        var service = new LocalBrowserService();
        var root = Path.Combine(_tempRoot, "local");
        var subDir = Path.Combine(root, "a-folder");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(root, "a-file.txt");
        await File.WriteAllTextAsync(filePath, "content");
        #endregion

        #region Initial Assert
        Assert.True(Directory.Exists(root));
        #endregion

        #region Act
        var entries = await service.ListDirectoryAsync(root, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, x => x.IsDirectory && x.Name == "a-folder");
        Assert.Contains(entries, x => !x.IsDirectory && x.Name == "a-file.txt" && x.Length == 7);
        #endregion
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
