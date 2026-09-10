using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;

namespace AzureFilesSync.Tests;

public sealed class TransferCheckpointPolicyTests
{
    [Fact]
    public void IsUsable_MatchingDownloadCheckpointAndStagingFile_ReturnsTrue()
    {
        #region Arrange
        var jobId = Guid.NewGuid();
        var remotePath = new SharePath("account", "share", "folder/file.bin");
        var request = new TransferRequest(TransferDirection.Download, @"C:\downloads\file.bin", remotePath);
        var checkpoint = new TransferCheckpoint(
            jobId,
            request.Direction,
            request.LocalPath,
            remotePath,
            1024,
            512,
            DateTimeOffset.UtcNow,
            "etag-1",
            @"C:\downloads\.file.bin.partial");
        #endregion

        #region Initial Assert
        Assert.Equal("etag-1", checkpoint.SourceVersion);
        Assert.Equal(512, checkpoint.NextOffset);
        #endregion

        #region Act
        var usable = TransferCheckpointPolicy.IsUsable(
            checkpoint,
            request,
            1024,
            "etag-1",
            @"C:\downloads\.file.bin.partial",
            stagingFileExists: true);
        #endregion

        #region Assert
        Assert.True(usable);
        #endregion
    }

    [Theory]
    [InlineData("etag-2", true)]
    [InlineData("etag-1", false)]
    public void IsUsable_ChangedSourceOrMissingStagingFile_ReturnsFalse(
        string currentSourceVersion,
        bool stagingFileExists)
    {
        #region Arrange
        var remotePath = new SharePath("account", "share", "folder/file.bin");
        var request = new TransferRequest(TransferDirection.Download, @"C:\downloads\file.bin", remotePath);
        var checkpoint = new TransferCheckpoint(
            Guid.NewGuid(),
            request.Direction,
            request.LocalPath,
            remotePath,
            1024,
            512,
            DateTimeOffset.UtcNow,
            "etag-1",
            @"C:\downloads\.file.bin.partial");
        #endregion

        #region Initial Assert
        Assert.NotNull(checkpoint.SourceVersion);
        #endregion

        #region Act
        var usable = TransferCheckpointPolicy.IsUsable(
            checkpoint,
            request,
            1024,
            currentSourceVersion,
            @"C:\downloads\.file.bin.partial",
            stagingFileExists);
        #endregion

        #region Assert
        Assert.False(usable);
        #endregion
    }

    [Fact]
    public void GetUsableCompletedRanges_KeepsOnlyExactTransferChunks()
    {
        #region Arrange
        var request = new TransferRequest(
            TransferDirection.Upload,
            @"C:\uploads\file.bin",
            new SharePath("account", "container", "file.bin", RemoteProviderKind.AzureBlob),
            ChunkSizeBytes: 4);
        var checkpoint = new TransferCheckpoint(
            Guid.NewGuid(),
            request.Direction,
            request.LocalPath,
            request.RemotePath,
            10,
            8,
            DateTimeOffset.UtcNow,
            "source-version",
            CompletedRanges:
            [
                new TransferRangeCheckpoint(4, 4),
                new TransferRangeCheckpoint(0, 4),
                new TransferRangeCheckpoint(0, 4),
                new TransferRangeCheckpoint(8, 4),
                new TransferRangeCheckpoint(3, 4)
            ]);
        #endregion

        #region Initial Assert
        Assert.Equal(5, checkpoint.CompletedRanges!.Count);
        #endregion

        #region Act
        var ranges = TransferCheckpointPolicy.GetUsableCompletedRanges(checkpoint, totalBytes: 10, chunkSize: 4);
        #endregion

        #region Assert
        Assert.Equal(
            [new TransferRangeCheckpoint(0, 4), new TransferRangeCheckpoint(4, 4)],
            ranges);
        #endregion
    }
}
