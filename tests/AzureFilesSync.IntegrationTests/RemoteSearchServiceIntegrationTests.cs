using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Azure;
using Azure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureFilesSync.IntegrationTests;

public sealed class RemoteSearchServiceIntegrationTests
{
    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_RecursesDirectories_AndReturnsNameMatches()
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
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
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
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        var result = ToResult(updates);
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
    public async Task RemoteSearchService_SearchIncrementalAsync_ContinuesScanningAfterMaxResults_AndMarksTruncated()
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
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
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
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        var result = ToResult(updates);
        #endregion

        #region Assert
        Assert.True(result.IsTruncated);
        Assert.Single(result.Matches);
        Assert.Equal("file-1.log", result.Matches[0].Name);
        Assert.Equal(1, result.ScannedDirectories);
        Assert.Equal(2, result.ScannedEntries);
        Assert.Equal(2, browser.ListPageCalls);
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_MatchesOnFullPath_WhenNameDoesNotMatch()
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
                        new RemoteEntry("nexdata", "nexdata", true, 0, DateTimeOffset.UtcNow),
                        new RemoteEntry("misc", "misc", true, 0, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    "nexdata" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("2026-archive.bin", "nexdata/2026-archive.bin", false, 20, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    "misc" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("ignore.txt", "misc/ignore.txt", false, 5, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    _ => new RemoteDirectoryPage([], null, false)
                };
            }
        };
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "nexdata",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        var result = ToResult(updates);
        #endregion

        #region Assert
        Assert.False(result.IsTruncated);
        Assert.Contains(result.Matches, x => x.FullPath == "nexdata/2026-archive.bin");
        Assert.DoesNotContain(result.Matches, x => x.FullPath == "misc/ignore.txt");
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_SkipsDeniedSubdirectory_AndContinuesTraversal()
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
                        new RemoteEntry("restricted", "restricted", true, 0, DateTimeOffset.UtcNow),
                        new RemoteEntry("open", "open", true, 0, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    "restricted" => throw new RequestFailedException(
                        403,
                        "Denied",
                        "AuthorizationPermissionMismatch",
                        null),
                    "open" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("user-data.log", "open/user-data.log", false, 10, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    _ => new RemoteDirectoryPage([], null, false)
                };
            }
        };
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "user",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        var result = ToResult(updates);
        #endregion

        #region Assert
        Assert.False(result.IsTruncated);
        Assert.Contains(result.Matches, x => x.FullPath == "open/user-data.log");
        Assert.Equal(3, result.ScannedDirectories);
        Assert.Equal(3, browser.ListPageCalls);
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_SkipsDeniedSubdirectory_WhenFailureIsWrappedInAggregateException()
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
                        new RemoteEntry("restricted", "restricted", true, 0, DateTimeOffset.UtcNow),
                        new RemoteEntry("open", "open", true, 0, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    "restricted" => throw new AggregateException(
                        new RequestFailedException(
                            403,
                            "Denied",
                            "AuthorizationPermissionMismatch",
                            null)),
                    "open" => new RemoteDirectoryPage(
                    [
                        new RemoteEntry("user-result.log", "open/user-result.log", false, 10, DateTimeOffset.UtcNow)
                    ],
                    null,
                    false),
                    _ => new RemoteDirectoryPage([], null, false)
                };
            }
        };
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "user",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        var result = ToResult(updates);
        #endregion

        #region Assert
        Assert.False(result.IsTruncated);
        Assert.Contains(result.Matches, x => x.FullPath == "open/user-result.log");
        Assert.Equal(3, result.ScannedDirectories);
        Assert.Equal(3, browser.ListPageCalls);
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_SkipsTimedOutSubdirectory_AndContinuesTraversal()
    {
        #region Arrange
        var browser = new StubAzureFilesBrowserService
        {
            ListDirectoryPageAsyncBehavior = (path, _, _, _) =>
            {
                var normalized = path.NormalizeRelativePath();
                return normalized switch
                {
                    "" => Task.FromResult(
                        new RemoteDirectoryPage(
                        [
                            new RemoteEntry("slow", "slow", true, 0, DateTimeOffset.UtcNow),
                            new RemoteEntry("open", "open", true, 0, DateTimeOffset.UtcNow)
                        ],
                        null,
                        false)),
                    "slow" => Task.FromException<RemoteDirectoryPage>(new OperationCanceledException("Simulated directory timeout.")),
                    "open" => Task.FromResult(
                        new RemoteDirectoryPage(
                        [
                            new RemoteEntry("user-result.log", "open/user-result.log", false, 10, DateTimeOffset.UtcNow)
                        ],
                        null,
                        false)),
                    _ => Task.FromResult(new RemoteDirectoryPage([], null, false))
                };
            }
        };
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "user",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        var result = ToResult(updates);
        #endregion

        #region Assert
        Assert.False(result.IsTruncated);
        Assert.Contains(result.Matches, x => x.FullPath == "open/user-result.log");
        Assert.Equal(3, result.ScannedDirectories);
        Assert.Equal(3, browser.ListPageCalls);
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_EmitsProgressBeforeCompletion()
    {
        #region Arrange
        var entries = Enumerable.Range(1, 60)
            .Select(i => new RemoteEntry($"User-{i}.txt", $"User-{i}.txt", false, 10, DateTimeOffset.UtcNow))
            .ToList();
        var browser = new StubAzureFilesBrowserService
        {
            ListDirectoryPageBehavior = (_, _, _) => new RemoteDirectoryPage(entries, null, false)
        };
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "User",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        #endregion

        #region Assert
        Assert.True(updates.Count >= 2);
        Assert.Contains(updates, x => !x.IsCompleted);
        Assert.True(updates[^1].IsCompleted);
        Assert.Equal(60, updates[^1].TotalMatches);
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_FlushesBufferedMatches_DuringNonMatchTraversal()
    {
        #region Arrange
        var browser = new StubAzureFilesBrowserService
        {
            ListDirectoryPageAsyncBehavior = async (_, continuation, _, cancellationToken) =>
            {
                if (continuation is null)
                {
                    return new RemoteDirectoryPage(
                    [
                        new RemoteEntry("User-A.txt", "User-A.txt", false, 10, DateTimeOffset.UtcNow),
                        new RemoteEntry("User-B.txt", "User-B.txt", false, 10, DateTimeOffset.UtcNow)
                    ],
                    "page-2",
                    true);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
                return new RemoteDirectoryPage(
                [
                    new RemoteEntry("alpha.txt", "alpha.txt", false, 10, DateTimeOffset.UtcNow),
                    new RemoteEntry("beta.txt", "beta.txt", false, 10, DateTimeOffset.UtcNow),
                    new RemoteEntry("gamma.txt", "gamma.txt", false, 10, DateTimeOffset.UtcNow)
                ],
                null,
                false);
            }
        };
        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "User",
            IncludeDirectories: false,
            MaxResults: 1000);
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var updates = await CollectUpdatesAsync(service, request, CancellationToken.None);
        #endregion

        #region Assert
        Assert.True(updates.Count >= 3);
        Assert.Contains(
            updates,
            x => !x.IsCompleted &&
                 x.NewMatches.Count == 2 &&
                 x.ScannedEntries >= 5);
        Assert.True(updates[^1].IsCompleted);
        Assert.Equal(2, updates[^1].TotalMatches);
        Assert.Equal(5, updates[^1].ScannedEntries);
        #endregion
    }

    [Fact]
    public async Task RemoteSearchService_SearchIncrementalAsync_ThrowsWhenCanceledMidSearch()
    {
        #region Arrange
        var rootEntries = Enumerable.Range(1, 50)
            .Select(i => new RemoteEntry($"User-{i}.txt", $"User-{i}.txt", false, 10, DateTimeOffset.UtcNow))
            .ToList();
        rootEntries.Add(new RemoteEntry("sub", "sub", true, 0, DateTimeOffset.UtcNow));

        var browser = new StubAzureFilesBrowserService
        {
            ListDirectoryPageAsyncBehavior = async (path, _, _, cancellationToken) =>
            {
                if (string.Equals(path.NormalizeRelativePath(), "sub", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    return new RemoteDirectoryPage([], null, false);
                }

                return new RemoteDirectoryPage(rootEntries, null, false);
            }
        };

        var service = new RemoteSearchService(browser, NullLogger<RemoteSearchService>.Instance);
        var request = new RemoteSearchRequest(
            new SharePath("storage", "share", string.Empty),
            "User",
            IncludeDirectories: true,
            MaxResults: 1000);
        var cts = new CancellationTokenSource();
        var sawUpdate = false;
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.ListPageCalls);
        #endregion

        #region Act
        var action = async () =>
        {
            await foreach (var update in service.SearchIncrementalAsync(request, cts.Token))
            {
                if (!sawUpdate)
                {
                    sawUpdate = true;
                    cts.Cancel();
                }
            }
        };
        #endregion

        #region Assert
        await Assert.ThrowsAsync<OperationCanceledException>(action);
        Assert.True(sawUpdate);
        #endregion
    }

    private static async Task<List<RemoteSearchProgress>> CollectUpdatesAsync(
        RemoteSearchService service,
        RemoteSearchRequest request,
        CancellationToken cancellationToken)
    {
        var updates = new List<RemoteSearchProgress>();
        await foreach (var progress in service.SearchIncrementalAsync(request, cancellationToken))
        {
            updates.Add(progress);
        }

        return updates;
    }

    private static RemoteSearchResult ToResult(IReadOnlyList<RemoteSearchProgress> updates)
    {
        var matches = updates.SelectMany(x => x.NewMatches).ToList();
        var final = updates.LastOrDefault() ?? new RemoteSearchProgress([], 0, IsCompleted: true, IsTruncated: false, 0, 0);
        return new RemoteSearchResult(matches, final.IsTruncated, final.ScannedDirectories, final.ScannedEntries);
    }

    private sealed class StubAzureFilesBrowserService : IAzureFilesBrowserService
    {
        public int ListPageCalls { get; private set; }

        public Func<SharePath, string?, int, RemoteDirectoryPage> ListDirectoryPageBehavior { get; set; } =
            (_, _, _) => new RemoteDirectoryPage([], null, false);
        public Func<SharePath, string?, int, CancellationToken, Task<RemoteDirectoryPage>>? ListDirectoryPageAsyncBehavior { get; set; }

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([]);

        public Task<RemoteDirectoryPage> ListDirectoryPageAsync(
            SharePath path,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken)
        {
            ListPageCalls++;
            return ListDirectoryPageAsyncBehavior is not null
                ? ListDirectoryPageAsyncBehavior(path, continuationToken, pageSize, cancellationToken)
                : Task.FromResult(ListDirectoryPageBehavior(path, continuationToken, pageSize));
        }

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);
    }
}
