using Azure;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Azure;
using System.Net.Http;
using System.Net.Sockets;

namespace AzureFilesSync.IntegrationTests;

public sealed class RemoteCapabilityServiceIntegrationTests
{
    private static readonly RemoteContext ValidContext = new("storage", "share", string.Empty, "sub");

    [Fact]
    public async Task RemoteCapabilityService_MapsAuthorizationPermissionMismatch_ToPermissionDenied()
    {
        #region Arrange
        var browser = new TestAzureFilesBrowserService
        {
            Behavior = _ => throw new RequestFailedException(403, "forbidden", "AuthorizationPermissionMismatch", null)
        };
        var service = new RemoteCapabilityService(browser, new RemoteErrorInterpreter());
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.CallCount);
        #endregion

        #region Act
        var snapshot = await service.RefreshAsync(ValidContext, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(RemoteAccessState.PermissionDenied, snapshot.State);
        Assert.False(snapshot.CanBrowse);
        Assert.Equal("AuthorizationPermissionMismatch", snapshot.ErrorCode);
        Assert.Equal(403, snapshot.HttpStatus);
        #endregion
    }

    [Fact]
    public async Task RemoteCapabilityService_MapsNotFound_AndTransientFailures()
    {
        #region Arrange
        var browser = new TestAzureFilesBrowserService();
        var service = new RemoteCapabilityService(browser, new RemoteErrorInterpreter());
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.CallCount);
        #endregion

        #region Act
        browser.Behavior = _ => throw new RequestFailedException(404, "missing", "ResourceNotFound", null);
        var notFound = await service.RefreshAsync(ValidContext with { Path = "missing" }, CancellationToken.None);

        browser.Behavior = _ => throw new RequestFailedException(503, "busy", "ServerBusy", null);
        var transient = await service.RefreshAsync(ValidContext with { Path = "retry" }, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(RemoteAccessState.NotFound, notFound.State);
        Assert.Equal(404, notFound.HttpStatus);
        Assert.Equal(RemoteAccessState.TransientFailure, transient.State);
        Assert.Equal(503, transient.HttpStatus);
        #endregion
    }

    [Fact]
    public async Task RemoteCapabilityService_MapsDnsHostNotFound_ToEndpointUnavailable()
    {
        #region Arrange
        var browser = new TestAzureFilesBrowserService
        {
            Behavior = _ =>
            {
                var socket = new SocketException((int)SocketError.HostNotFound);
                var http = new HttpRequestException("No such host is known. (storage.file.core.windows.net:443)", socket);
                throw new RequestFailedException("No such host is known. (storage.file.core.windows.net:443)", http);
            }
        };
        var service = new RemoteCapabilityService(browser, new RemoteErrorInterpreter());
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.CallCount);
        #endregion

        #region Act
        var snapshot = await service.RefreshAsync(ValidContext, CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(RemoteAccessState.EndpointUnavailable, snapshot.State);
        Assert.Equal("DnsHostNotFound", snapshot.ErrorCode);
        Assert.Contains("file.core.windows.net", snapshot.UserMessage, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task RemoteCapabilityService_EvaluateUsesCache_AndRefreshBypassesCache()
    {
        #region Arrange
        var browser = new TestAzureFilesBrowserService
        {
            Behavior = _ => []
        };
        var service = new RemoteCapabilityService(browser, new RemoteErrorInterpreter());
        #endregion

        #region Initial Assert
        Assert.Equal(0, browser.CallCount);
        #endregion

        #region Act
        var first = await service.EvaluateAsync(ValidContext, CancellationToken.None);
        var second = await service.EvaluateAsync(ValidContext, CancellationToken.None);
        var callsAfterEvaluate = browser.CallCount;

        browser.Behavior = _ => throw new RequestFailedException(503, "busy", "ServerBusy", null);
        var refreshed = await service.RefreshAsync(ValidContext, CancellationToken.None);
        var lastKnown = service.GetLastKnown(ValidContext);
        #endregion

        #region Assert
        Assert.Equal(RemoteAccessState.Accessible, first.State);
        Assert.Equal(RemoteAccessState.Accessible, second.State);
        Assert.Equal(1, callsAfterEvaluate);

        Assert.Equal(RemoteAccessState.TransientFailure, refreshed.State);
        Assert.Equal(2, browser.CallCount);
        Assert.NotNull(lastKnown);
        Assert.Equal(RemoteAccessState.TransientFailure, lastKnown!.State);
        #endregion
    }

    private sealed class TestAzureFilesBrowserService : IAzureFilesBrowserService
    {
        public Func<SharePath, IReadOnlyList<RemoteEntry>> Behavior { get; set; } = _ => [];
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Behavior(path));
        }

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);
    }
}
