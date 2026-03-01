using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Azure;

namespace AzureFilesSync.IntegrationTests;

public sealed class RemoteSearchServiceIntegrationTests
{
    [Fact]
    public async Task RemoteSearchService_SearchAsync_RecursesDirectories_AndReturnsNameMatches()
    {
        #region Arrange
        var browser = new StubAzureFilesBrowserService
        {
            ListDirectoryPageBehavior = (path, _, _) =>
            {
                var normalized = path.NormalizeRelativePath();
                return normalized switch
                {
                    "" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("logs", "logs", true, 0, DateTimeOffset.UtcNow),
                        new RemoteEntry("readme.txt", "readme.txt", false, 10, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    "logs" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("app.log", "logs/app.log", false, 20, DateTimeOffset.UtcNow),
                        new RemoteEntry("archive", "logs/archive", true, 0, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    "logs/archive" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("app-2025.log", "logs/archive/app-2025.log", false, 30, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    _ => new RemoteDirectoryPage([], null, false)
                };
            }
        };
        var service = new RemoteSearchService(browser);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "app",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var result = await service.SearchAsync(request, CancellationToken.None);
        #endregion

        #region Assert
        Assert.False(result.IsTruncated);
        Assert.Equal(3, result.ScannedDirectories);
        Assert.Equal(5, result.ScannedEntries);
        Assert.Equal(2, result.Matches.Count);
        Assert.Contains(result.Matches, x => x.FullPath == "logs/app.log");
        Assert.Contains(result.Matches, x => x.FullPath == "logs/archive/app-2025.log");
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchAsync_StopsAtMaxResults_AndMarksTruncated()
    {
        #region Arrange
        var browser = new StubAzureFilesBrowserService
        {
            ListDirectoryPageBehavior = (_, continuation, _) =>
                continuation is null
                    ? new RemoteDirectoryPage(
                    [
                        new RemoteEntry("file-1.log", "file-1.log", false, 10, DateTimeOffset.UtcNow)
                    ],
                    "page-2",
                    true)
                    : new RemoteDirectoryPage(
                    [
                        new RemoteEntry("file-2.log", "file-2.log", false, 10, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false)
        };
        var service = new RemoteSearchService(browser);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "file",
            IncludeDirectories: false,
            MaxResults: 1);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var result = await service.SearchAsync(request, CancellationToken.None);
        #endregion

        #region Assert
        Assert.True(result.IsTruncated);
        Assert.Single(result.Matches);
        Assert.Equal("file-1.log", result.Matches[0].Name);
        Assert.Equal(1, result.ScannedDirectories);
        Assert.Equal(1, result.ScannedEntries);
        Assert.Equal(1, browser.ListPageCalls);
        #endregion
    }

    private sealed class StubAzureFilesBrowserService : IAzureFilesBrowserService
    {
        public int ListPageCalls { get; private set; }

        public Func<SharePath, string?, int, RemoteDirectoryPage> ListDirectoryPageBehavior { get; set; } =
            (_, _, _) => new RemoteDirectoryPage([], null, false);

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([]);

        public Task<RemoteDirectoryPage> ListDirectoryPageAsync(
            SharePath path,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken)
        {
            ListPageCalls++;
            return Task.FromResult(ListDirectoryPageBehavior(path, continuationToken, pageSize));
        }

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);
    }
}
