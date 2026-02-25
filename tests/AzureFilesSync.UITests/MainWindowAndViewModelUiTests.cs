using Azure.Core;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Models;
using AzureFilesSync.Desktop.ViewModels;
using Xunit;

namespace AzureFilesSync.UITests;

public sealed class MainWindowAndViewModelUiTests
{
    [Fact]
    public async Task MainViewModel_QueueCommands_CallTransferQueueService()
    {
        #region Arrange
        var viewModel = CreateViewModel(out var queue);
        var snapshot = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(TransferDirection.Upload, @"C:\work\file.txt", new SharePath("storage", "share", "file.txt")),
            TransferJobStatus.Running,
            25,
            100,
            null,
            0);

        viewModel.QueueItems.Add(new QueueItemView { Snapshot = snapshot });
        viewModel.SelectedQueueItem = viewModel.QueueItems[0];
        #endregion

        #region Initial Assert
        Assert.NotNull(viewModel.SelectedQueueItem);
        #endregion

        #region Act
        await viewModel.PauseSelectedJobCommand.ExecuteAsync(null);
        await viewModel.ResumeSelectedJobCommand.ExecuteAsync(null);
        await viewModel.RetrySelectedJobCommand.ExecuteAsync(null);
        await viewModel.CancelSelectedJobCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Contains(snapshot.JobId, queue.PausedJobIds);
        Assert.Contains(snapshot.JobId, queue.ResumedJobIds);
        Assert.Contains(snapshot.JobId, queue.RetriedJobIds);
        Assert.Contains(snapshot.JobId, queue.CanceledJobIds);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_RemotePermissionDenied_DisablesRemoteCommands_AndShowsInfoMessage()
    {
        #region Arrange
        var capabilityService = new TestRemoteCapabilityService(new RemoteCapabilitySnapshot(
            RemoteAccessState.PermissionDenied,
            CanBrowse: false,
            CanUpload: false,
            CanDownload: false,
            CanPlanMirror: false,
            CanExecuteMirror: false,
            UserMessage: "No Azure Files data permission.",
            EvaluatedUtc: DateTimeOffset.UtcNow,
            ErrorCode: "AuthorizationPermissionMismatch",
            HttpStatus: 403));
        var viewModel = CreateViewModel(capabilityService, out _);
        SetValidRemoteSelection(viewModel);
        viewModel.SelectedLocalEntry = new LocalEntry("local.txt", @"C:\work\local.txt", false, 10, DateTimeOffset.UtcNow);
        viewModel.SelectedRemoteEntry = new RemoteEntry("remote.txt", "remote.txt", false, 20, DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        Assert.True(viewModel.SelectedStorageAccount is not null);
        Assert.True(viewModel.SelectedFileShare is not null);
        #endregion

        #region Act
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Contains("No Azure Files data permission", viewModel.RemotePaneStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.BuildMirrorPlanCommand.CanExecute(null));
        Assert.False(viewModel.EnqueueUploadCommand.CanExecute(null));
        Assert.False(viewModel.EnqueueDownloadCommand.CanExecute(null));
        #endregion
    }

    [Fact]
    public async Task MainViewModel_RemoteCapabilityRecovery_ClearsInfoMessage_AndReEnablesCommands()
    {
        #region Arrange
        var capabilityService = new TestRemoteCapabilityService(new RemoteCapabilitySnapshot(
            RemoteAccessState.PermissionDenied,
            CanBrowse: false,
            CanUpload: false,
            CanDownload: false,
            CanPlanMirror: false,
            CanExecuteMirror: false,
            UserMessage: "No Azure Files data permission.",
            EvaluatedUtc: DateTimeOffset.UtcNow,
            ErrorCode: "AuthorizationPermissionMismatch",
            HttpStatus: 403));
        var viewModel = CreateViewModel(capabilityService, out _);
        SetValidRemoteSelection(viewModel);
        viewModel.SelectedLocalEntry = new LocalEntry("local.txt", @"C:\work\local.txt", false, 10, DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        Assert.False(viewModel.BuildMirrorPlanCommand.CanExecute(null));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.RemotePaneStatusMessage));
        #endregion

        #region Act
        capabilityService.SetSnapshot(RemoteCapabilitySnapshot.Accessible());
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        viewModel.SelectedRemoteEntry = new RemoteEntry("remote.txt", "remote.txt", false, 20, DateTimeOffset.UtcNow);
        #endregion

        #region Assert
        Assert.Equal(string.Empty, viewModel.RemotePaneStatusMessage);
        Assert.True(viewModel.BuildMirrorPlanCommand.CanExecute(null));
        Assert.True(viewModel.EnqueueUploadCommand.CanExecute(null));
        Assert.True(viewModel.EnqueueDownloadCommand.CanExecute(null));
        #endregion
    }

    private static MainViewModel CreateViewModel(IRemoteCapabilityService remoteCapabilityService, out SpyTransferQueueService queue)
    {
        queue = new SpyTransferQueueService();
        return new MainViewModel(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            remoteCapabilityService,
            new StubRemoteActionPolicyService());
    }

    private static MainViewModel CreateViewModel(out SpyTransferQueueService queue) =>
        CreateViewModel(new StubRemoteCapabilityService(), out queue);

    private static void SetValidRemoteSelection(MainViewModel viewModel)
    {
        viewModel.SelectedSubscription = new SubscriptionItem("sub", "Subscription");
        viewModel.SelectedStorageAccount = new StorageAccountItem("sub", "storage", "rg");
        viewModel.SelectedFileShare = new FileShareItem("share");
    }

    private sealed class SpyTransferQueueService : ITransferQueueService
    {
        public event EventHandler<TransferJobSnapshot>? JobUpdated;
        public List<Guid> PausedJobIds { get; } = [];
        public List<Guid> ResumedJobIds { get; } = [];
        public List<Guid> RetriedJobIds { get; } = [];
        public List<Guid> CanceledJobIds { get; } = [];

        public Guid Enqueue(TransferRequest request, bool startImmediately = true)
        {
            var id = Guid.NewGuid();
            var status = startImmediately ? TransferJobStatus.Queued : TransferJobStatus.Paused;
            JobUpdated?.Invoke(this, new TransferJobSnapshot(id, request, status, 0, 0, null, 0));
            return id;
        }

        public Task PauseAsync(Guid jobId, CancellationToken cancellationToken)
        {
            PausedJobIds.Add(jobId);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(Guid jobId, CancellationToken cancellationToken)
        {
            ResumedJobIds.Add(jobId);
            return Task.CompletedTask;
        }

        public Task RunQueuedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RetryAsync(Guid jobId, CancellationToken cancellationToken)
        {
            RetriedJobIds.Add(jobId);
            return Task.CompletedTask;
        }

        public Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
        {
            CanceledJobIds.Add(jobId);
            return Task.CompletedTask;
        }

        public IReadOnlyList<TransferJobSnapshot> Snapshot() => [];
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        public TokenCredential GetCredential() => throw new NotSupportedException();
        public Task<LoginSession> SignInInteractiveAsync(CancellationToken cancellationToken) => Task.FromResult(new LoginSession(true, "tester", "tenant"));
        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubDiscoveryService : IAzureDiscoveryService
    {
        public async IAsyncEnumerable<SubscriptionItem> ListSubscriptionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new SubscriptionItem("sub", "Subscription");
        }

        public async IAsyncEnumerable<StorageAccountItem> ListStorageAccountsAsync(string subscriptionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new StorageAccountItem(subscriptionId, "storage", "rg");
        }

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new FileShareItem("share");
        }
    }

    private sealed class StubLocalBrowserService : ILocalBrowserService
    {
        public Task<IReadOnlyList<LocalEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalEntry>>([]);

        public Task<LocalEntry?> GetEntryDetailsAsync(string fullPath, CancellationToken cancellationToken) =>
            Task.FromResult<LocalEntry?>(null);
    }

    private sealed class StubAzureBrowserService : IAzureFilesBrowserService
    {
        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([]);

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteEntry?>(null);
    }

    private sealed class StubLocalFileOperationsService : ILocalFileOperationsService
    {
        public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenWithAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubRemoteFileOperationsService : IRemoteFileOperationsService
    {
        public Task RenameAsync(SharePath path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(SharePath path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubMirrorPlannerService : IMirrorPlannerService
    {
        public Task<MirrorPlan> BuildPlanAsync(MirrorSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult(new MirrorPlan([]));
    }

    private sealed class StubMirrorExecutionService : IMirrorExecutionService
    {
        public Task<MirrorExecutionResult> ExecuteAsync(MirrorPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(new MirrorExecutionResult(0, 0, []));
    }

    private sealed class InMemoryConnectionProfileStore : IConnectionProfileStore
    {
        private ConnectionProfile _profile = ConnectionProfile.Empty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        public Task<ConnectionProfile> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_profile);

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            _profile = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class StubRemoteCapabilityService : IRemoteCapabilityService
    {
        private static readonly RemoteCapabilitySnapshot Accessible = new(
            RemoteAccessState.Accessible,
            CanBrowse: true,
            CanUpload: true,
            CanDownload: true,
            CanPlanMirror: true,
            CanExecuteMirror: true,
            UserMessage: string.Empty,
            EvaluatedUtc: DateTimeOffset.UtcNow,
            ErrorCode: null,
            HttpStatus: null);

        public Task<RemoteCapabilitySnapshot> EvaluateAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Accessible);

        public Task<RemoteCapabilitySnapshot> RefreshAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Accessible);

        public RemoteCapabilitySnapshot? GetLastKnown(RemoteContext context) => Accessible;
    }

    private sealed class TestRemoteCapabilityService : IRemoteCapabilityService
    {
        private RemoteCapabilitySnapshot _snapshot;

        public TestRemoteCapabilityService(RemoteCapabilitySnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public void SetSnapshot(RemoteCapabilitySnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<RemoteCapabilitySnapshot> EvaluateAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);

        public Task<RemoteCapabilitySnapshot> RefreshAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);

        public RemoteCapabilitySnapshot? GetLastKnown(RemoteContext context) => _snapshot;
    }

    private sealed class StubRemoteActionPolicyService : IRemoteActionPolicyService
    {
        public RemoteActionPolicy Compute(RemoteCapabilitySnapshot? capability, RemoteActionInputs inputs) =>
            new(
                CanEnqueueUpload: capability?.State == RemoteAccessState.Accessible && inputs.HasSelectedLocalFile,
                CanEnqueueDownload: capability?.State == RemoteAccessState.Accessible && inputs.HasSelectedRemoteFile,
                CanPlanMirror: capability?.State == RemoteAccessState.Accessible && !inputs.IsMirrorPlanning,
                CanExecuteMirror: capability?.State == RemoteAccessState.Accessible && inputs.HasMirrorPlan);
    }
}
