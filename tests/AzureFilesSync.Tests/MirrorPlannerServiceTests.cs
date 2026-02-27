using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;

namespace AzureFilesSync.Tests;

public sealed class MirrorPlannerServiceTests
{
    [Fact]
    public async Task BuildPlan_UploadWithDeleteEnabled_ProducesCreateUpdateDelete()
    {
        #region Arrange
        var localBrowser = new StubLocalBrowserService(new Dictionary<string, IReadOnlyList<LocalEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:/local"] =
            [
                new LocalEntry("new.txt", "C:/local/new.txt", false, 20, new DateTimeOffset(2026, 02, 24, 10, 0, 0, TimeSpan.Zero)),
                new LocalEntry("changed.txt", "C:/local/changed.txt", false, 50, new DateTimeOffset(2026, 02, 24, 11, 0, 0, TimeSpan.Zero))
            ]
        });

        var remoteBrowser = new StubAzureFilesBrowserService(new Dictionary<string, IReadOnlyList<RemoteEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] =
            [
                new RemoteEntry("changed.txt", "changed.txt", false, 10, new DateTimeOffset(2026, 02, 24, 9, 0, 0, TimeSpan.Zero)),
                new RemoteEntry("delete.txt", "delete.txt", false, 30, new DateTimeOffset(2026, 02, 24, 8, 0, 0, TimeSpan.Zero))
            ]
        });

        var service = new MirrorPlannerService(localBrowser, remoteBrowser);
        var spec = new MirrorSpec(TransferDirection.Upload, "C:/local", new SharePath("acct", "share", string.Empty), IncludeDeletes: true);
        #endregion

        #region Initial Assert
        Assert.NotNull(service);
        Assert.Equal(2, (await localBrowser.ListDirectoryAsync("C:/local", CancellationToken.None)).Count);
        Assert.Equal(2, (await remoteBrowser.ListDirectoryAsync(new SharePath("acct", "share", string.Empty), CancellationToken.None)).Count);
        #endregion

        #region Act
        var plan = await service.BuildPlanAsync(spec, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(1, plan.CreateCount);
        Assert.Equal(1, plan.UpdateCount);
        Assert.Equal(1, plan.DeleteCount);
        Assert.Contains(plan.Items, x => x.RelativePath == "new.txt" && x.Action == MirrorActionType.Create);
        Assert.Contains(plan.Items, x => x.RelativePath == "changed.txt" && x.Action == MirrorActionType.Update);
        Assert.Contains(plan.Items, x => x.RelativePath == "delete.txt" && x.Action == MirrorActionType.Delete);
        #endregion
    }

    [Fact]
    public async Task BuildPlan_UploadWithDeleteDisabled_ConvertsDeleteToSkip()
    {
        #region Arrange
        var localBrowser = new StubLocalBrowserService(new Dictionary<string, IReadOnlyList<LocalEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:/local"] = [new LocalEntry("same.txt", "C:/local/same.txt", false, 20, new DateTimeOffset(2026, 02, 24, 10, 0, 0, TimeSpan.Zero))]
        });

        var remoteBrowser = new StubAzureFilesBrowserService(new Dictionary<string, IReadOnlyList<RemoteEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] =
            [
                new RemoteEntry("same.txt", "same.txt", false, 20, new DateTimeOffset(2026, 02, 24, 10, 0, 0, TimeSpan.Zero)),
                new RemoteEntry("orphan.txt", "orphan.txt", false, 10, new DateTimeOffset(2026, 02, 23, 10, 0, 0, TimeSpan.Zero))
            ]
        });

        var service = new MirrorPlannerService(localBrowser, remoteBrowser);
        var spec = new MirrorSpec(TransferDirection.Upload, "C:/local", new SharePath("acct", "share", string.Empty), IncludeDeletes: false);
        #endregion

        #region Initial Assert
        Assert.Single(await localBrowser.ListDirectoryAsync("C:/local", CancellationToken.None));
        Assert.Equal(2, (await remoteBrowser.ListDirectoryAsync(new SharePath("acct", "share", string.Empty), CancellationToken.None)).Count);
        #endregion

        #region Act
        var plan = await service.BuildPlanAsync(spec, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(0, plan.DeleteCount);
        Assert.Contains(plan.Items, x => x.RelativePath == "orphan.txt" && x.Action == MirrorActionType.Skip);
        #endregion
    }

    private sealed class StubLocalBrowserService : ILocalBrowserService
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<LocalEntry>> _map;

        public StubLocalBrowserService(IReadOnlyDictionary<string, IReadOnlyList<LocalEntry>> map)
        {
            _map = map;
        }

        public Task<IReadOnlyList<LocalEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            _map.TryGetValue(path, out var entries);
            return Task.FromResult(entries ?? (IReadOnlyList<LocalEntry>)[]);
        }

        public Task<LocalEntry?> GetEntryDetailsAsync(string fullPath, CancellationToken cancellationToken) =>
            Task.FromResult<LocalEntry?>(null);
    }

    private sealed class StubAzureFilesBrowserService : IAzureFilesBrowserService
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<RemoteEntry>> _map;

        public StubAzureFilesBrowserService(IReadOnlyDictionary<string, IReadOnlyList<RemoteEntry>> map)
        {
            _map = map;
        }

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken)
        {
            _map.TryGetValue(path.NormalizeRelativePath(), out var entries);
            return Task.FromResult(entries ?? (IReadOnlyList<RemoteEntry>)[]);
        }

        public Task<RemoteDirectoryPage> ListDirectoryPageAsync(SharePath path, string? continuationToken, int pageSize, CancellationToken cancellationToken)
        {
            _map.TryGetValue(path.NormalizeRelativePath(), out var entries);
            return Task.FromResult(new RemoteDirectoryPage(entries ?? [], null, false));
        }

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);
    }
}
