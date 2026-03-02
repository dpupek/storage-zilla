using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Tests;

public sealed class RemoteRootModelsTests
{
    [Fact]
    public void FileShareItem_FileShareKind_MapsToAzureFilesProvider()
    {
        #region Arrange
        var root = new FileShareItem("team-share", RemoteRootKind.FileShare);
        #endregion

        #region Initial Assert
        Assert.Equal(RemoteRootKind.FileShare, root.Kind);
        #endregion

        #region Act
        var provider = root.ProviderKind;
        var display = root.DisplayName;
        #endregion

        #region Assert
        Assert.Equal(RemoteProviderKind.AzureFiles, provider);
        Assert.Contains("File Share", display, StringComparison.Ordinal);
        #endregion
    }

    [Fact]
    public void FileShareItem_BlobContainerKind_MapsToAzureBlobProvider()
    {
        #region Arrange
        var root = new FileShareItem("documents", RemoteRootKind.BlobContainer);
        #endregion

        #region Initial Assert
        Assert.Equal(RemoteRootKind.BlobContainer, root.Kind);
        #endregion

        #region Act
        var provider = root.ProviderKind;
        var display = root.DisplayName;
        #endregion

        #region Assert
        Assert.Equal(RemoteProviderKind.AzureBlob, provider);
        Assert.Contains("Blob Container", display, StringComparison.Ordinal);
        #endregion
    }
}
