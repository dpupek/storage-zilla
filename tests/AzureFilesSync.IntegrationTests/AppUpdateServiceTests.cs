using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;
using AzureFilesSync.Infrastructure.Updates;

namespace AzureFilesSync.IntegrationTests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task ValidateDownloadedUpdateAsync_HashMismatch_ReturnsInvalid()
    {
        #region Arrange
        var service = CreateServiceWithNoopReleaseClient();
        var candidate = new UpdateCandidate("1.2.3", "v1.2.3", DateTimeOffset.UtcNow, "StorageZilla.msix", "https://example/msix", "https://example/sha");
        var downloaded = new UpdateDownloadResult(candidate, @"C:\temp\missing.msix", "abc", "def", DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        Assert.NotEqual(downloaded.ExpectedSha256, downloaded.ActualSha256);
        #endregion

        #region Act
        var result = await service.ValidateDownloadedUpdateAsync(downloaded, CancellationToken.None);
        #endregion

        #region Assert
        Assert.False(result.IsValid);
        Assert.Contains("hash", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task ValidateDownloadedUpdateAsync_ValidMsixPublisherAndVersion_ReturnsValid()
    {
        #region Arrange
        var service = CreateServiceWithNoopReleaseClient();
        var tempFile = Path.Combine(Path.GetTempPath(), $"storage-zilla-update-{Guid.NewGuid():N}.msix");
        CreateMsixWithManifest(tempFile, publisher: "CN=Danm@de Software", version: "1.2.3.0");
        var candidate = new UpdateCandidate("1.2.3", "v1.2.3", DateTimeOffset.UtcNow, Path.GetFileName(tempFile), "https://example/msix", "https://example/sha");
        var downloaded = new UpdateDownloadResult(candidate, tempFile, "abc", "abc", DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        Assert.True(File.Exists(tempFile));
        #endregion

        #region Act
        var result = await service.ValidateDownloadedUpdateAsync(downloaded, CancellationToken.None);
        #endregion

        #region Assert
        Assert.True(result.IsValid);
        Assert.Equal("CN=Danm@de Software", result.Publisher);
        Assert.Equal("1.2.3.0", result.PackageVersion);
        #endregion

        File.Delete(tempFile);
    }

    [Fact]
    public async Task GitHubReleaseClient_ParsesLatestStableRelease()
    {
        #region Arrange
        const string payload = """
        {
          "tag_name":"v1.4.2",
          "draft":false,
          "prerelease":false,
          "published_at":"2026-02-26T00:00:00Z",
          "assets":[
            { "name":"StorageZilla_1.4.2_x64.msix", "browser_download_url":"https://example.com/a.msix", "size": 1234 },
            { "name":"SHA256SUMS.txt", "browser_download_url":"https://example.com/SHA256SUMS.txt", "size": 99 }
          ]
        }
        """;
        var client = new HttpClient(new StubHttpMessageHandler(payload))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var releaseClient = new GitHubReleaseClient(client, new UpdateOptions());
        #endregion

        #region Initial Assert
        Assert.NotNull(releaseClient);
        #endregion

        #region Act
        var release = await releaseClient.GetLatestStableReleaseAsync(CancellationToken.None);
        #endregion

        #region Assert
        Assert.NotNull(release);
        Assert.Equal("v1.4.2", release!.TagName);
        Assert.Equal(2, release.Assets.Count);
        Assert.Contains(release.Assets, x => x.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase));
        #endregion
    }

    private static AppUpdateService CreateServiceWithNoopReleaseClient()
    {
        var releaseClient = new StubReleaseClient();
        var httpClient = new HttpClient(new StubHttpMessageHandler("{}"));
        return new AppUpdateService(releaseClient, httpClient, new UpdateOptions());
    }

    private static void CreateMsixWithManifest(string path, string publisher, string version)
    {
        var xml = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="StorageZilla.Desktop" Publisher="{publisher}" Version="{version}" />
        </Package>
        """;

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("AppxManifest.xml");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(xml);
    }

    private sealed class StubReleaseClient : Core.Contracts.IGitHubReleaseClient
    {
        public Task<GitHubRelease?> GetLatestStableReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult<GitHubRelease?>(null);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _payload;

        public StubHttpMessageHandler(string payload)
        {
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload, Encoding.UTF8, "application/json")
            });
    }
}
