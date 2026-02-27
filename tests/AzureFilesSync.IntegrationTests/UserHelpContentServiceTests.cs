using AzureFilesSync.Infrastructure.Docs;

namespace AzureFilesSync.IntegrationTests;

public sealed class UserHelpContentServiceTests
{
    [Fact]
    public async Task LoadTopicAsync_WithValidTopic_ReturnsRenderedHtml()
    {
        #region Arrange
        var tempRoot = Path.Combine(Path.GetTempPath(), $"storage-zilla-help-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var readmePath = Path.Combine(tempRoot, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Storage Zilla Help\n\nThis is **markdown** content.");
        var service = new FileSystemUserHelpContentService(tempRoot);
        #endregion

        #region Initial Assert
        Assert.True(File.Exists(readmePath));
        #endregion

        #region Act
        var document = await service.LoadTopicAsync("overview", CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal("overview", document.TopicId);
        Assert.Contains("<strong>markdown</strong>", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<base href=", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(readmePath, document.SourcePath);
        #endregion
    }

    [Fact]
    public async Task LoadTopicAsync_WhenFileMissing_ThrowsFriendlyError()
    {
        #region Arrange
        var tempRoot = Path.Combine(Path.GetTempPath(), $"storage-zilla-help-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var service = new FileSystemUserHelpContentService(tempRoot);
        #endregion

        #region Initial Assert
        Assert.False(File.Exists(Path.Combine(tempRoot, "README.md")));
        #endregion

        #region Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoadTopicAsync("overview", CancellationToken.None));
        #endregion

        #region Assert
        Assert.Contains("Help document not found", exception.Message, StringComparison.OrdinalIgnoreCase);
        #endregion
    }
}
