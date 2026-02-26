using Azure.Core;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.Models;
using AzureFilesSync.Desktop.Services;
using AzureFilesSync.Desktop.ViewModels;
using System.Collections;
using System.IO;
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
        var snapshot2 = snapshot with { JobId = Guid.NewGuid() };

        viewModel.QueueItems.Add(new QueueItemView { Snapshot = snapshot });
        viewModel.QueueItems.Add(new QueueItemView { Snapshot = snapshot2 });
        viewModel.UpdateSelectedQueueSelection(new ArrayList { viewModel.QueueItems[0], viewModel.QueueItems[1] });
        #endregion

        #region Initial Assert
        Assert.Equal(2, viewModel.SelectedQueueCount);
        #endregion

        #region Act
        await viewModel.PauseSelectedJobCommand.ExecuteAsync(null);
        await viewModel.ResumeSelectedJobCommand.ExecuteAsync(null);
        await viewModel.RetrySelectedJobCommand.ExecuteAsync(null);
        await viewModel.CancelSelectedJobCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Contains(snapshot.JobId, queue.PausedJobIds);
        Assert.Contains(snapshot2.JobId, queue.PausedJobIds);
        Assert.Contains(snapshot.JobId, queue.ResumedJobIds);
        Assert.Contains(snapshot2.JobId, queue.ResumedJobIds);
        Assert.Contains(snapshot.JobId, queue.RetriedJobIds);
        Assert.Contains(snapshot2.JobId, queue.RetriedJobIds);
        Assert.Contains(snapshot.JobId, queue.CanceledJobIds);
        Assert.Contains(snapshot2.JobId, queue.CanceledJobIds);
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

    [Fact]
    public async Task MainViewModel_EnqueueUpload_UsesConfiguredTransferTuning()
    {
        #region Arrange
        var viewModel = CreateViewModel(out var queue);
        SetValidRemoteSelection(viewModel);
        viewModel.TransferMaxConcurrency = 7;
        viewModel.TransferMaxBytesPerSecond = 1048576;
        viewModel.SelectedLocalEntry = new LocalEntry("local.txt", @"C:\work\local.txt", false, 10, DateTimeOffset.UtcNow);
        #endregion

        #region Initial Assert
        Assert.True(viewModel.EnqueueUploadCommand.CanExecute(null));
        #endregion

        #region Act
        await viewModel.EnqueueUploadCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        var request = Assert.Single(queue.EnqueuedRequests);
        Assert.Equal(7, request.MaxConcurrency);
        Assert.Equal(1048576, request.MaxBytesPerSecond);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_PauseAllQueue_CallsTransferQueueService()
    {
        #region Arrange
        var viewModel = CreateViewModel(out var queue);
        #endregion

        #region Initial Assert
        Assert.Equal(0, queue.PauseAllCount);
        #endregion

        #region Act
        await viewModel.PauseAllQueueCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(1, queue.PauseAllCount);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_QueueLocalSelection_ConflictWithDefaultSkip_DoesNotEnqueue()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new AlwaysConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.UploadConflictDefaultPolicy = TransferConflictPolicy.Skip;
        var selected = new ArrayList
        {
            new LocalEntry("file.txt", @"C:\work\file.txt", false, 10, DateTimeOffset.UtcNow)
        };
        #endregion

        #region Initial Assert
        Assert.Empty(queue.EnqueuedRequests);
        #endregion

        #region Act
        await viewModel.QueueLocalSelectionAsync(selected, startImmediately: true);
        #endregion

        #region Assert
        Assert.Empty(queue.EnqueuedRequests);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_QueueLocalSelection_ConflictWithDefaultOverwrite_Enqueues()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new AlwaysConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.UploadConflictDefaultPolicy = TransferConflictPolicy.Overwrite;
        var selected = new ArrayList
        {
            new LocalEntry("file.txt", @"C:\work\file.txt", false, 10, DateTimeOffset.UtcNow)
        };
        #endregion

        #region Initial Assert
        Assert.Empty(queue.EnqueuedRequests);
        #endregion

        #region Act
        await viewModel.QueueLocalSelectionAsync(selected, startImmediately: true);
        #endregion

        #region Assert
        var request = Assert.Single(queue.EnqueuedRequests);
        Assert.Equal(TransferConflictPolicy.Overwrite, request.ConflictPolicy);
        #endregion
    }

    [Fact]
    public void QueueItemView_CompletedZeroByteTransfer_ShowsHundredPercent()
    {
        #region Arrange
        var snapshot = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(TransferDirection.Download, @"C:\work\zero.txt", new SharePath("storage", "share", "zero.txt")),
            TransferJobStatus.Completed,
            0,
            0,
            "Completed",
            0);
        var view = new QueueItemView { Snapshot = snapshot };
        #endregion

        #region Initial Assert
        Assert.Equal(TransferJobStatus.Completed, view.Status);
        #endregion

        #region Act
        var progress = view.ProgressDisplay;
        #endregion

        #region Assert
        Assert.Equal("100% (0/0)", progress);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_AskConflict_UsesPromptOverwrite_AndQueues()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new AlwaysConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Overwrite, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.UploadConflictDefaultPolicy = TransferConflictPolicy.Ask;
        var selected = new ArrayList { new LocalEntry("file.txt", @"C:\work\file.txt", false, 10, DateTimeOffset.UtcNow) };
        #endregion

        #region Initial Assert
        Assert.Empty(queue.EnqueuedRequests);
        #endregion

        #region Act
        await viewModel.QueueLocalSelectionAsync(selected, startImmediately: true);
        #endregion

        #region Assert
        var request = Assert.Single(queue.EnqueuedRequests);
        Assert.Equal(TransferConflictPolicy.Overwrite, request.ConflictPolicy);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_AskConflict_PromptCancelBatch_DoesNotQueue()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new AlwaysConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.CancelBatch, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.UploadConflictDefaultPolicy = TransferConflictPolicy.Ask;
        var selected = new ArrayList { new LocalEntry("file.txt", @"C:\work\file.txt", false, 10, DateTimeOffset.UtcNow) };
        #endregion

        #region Initial Assert
        Assert.Empty(queue.EnqueuedRequests);
        #endregion

        #region Act
        await viewModel.QueueLocalSelectionAsync(selected, startImmediately: true);
        #endregion

        #region Assert
        Assert.Empty(queue.EnqueuedRequests);
        #endregion
    }

    [Fact]
    public void MainViewModel_QueueFilters_FilterAndReset()
    {
        #region Arrange
        var viewModel = CreateViewModel(out _);
        var queuedUpload = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(TransferDirection.Upload, @"C:\work\queued.txt", new SharePath("storage", "share", "queued.txt")),
            TransferJobStatus.Queued,
            0,
            100,
            null,
            0);
        var failedDownload = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(TransferDirection.Download, @"C:\work\failed.txt", new SharePath("storage", "share", "failed.txt")),
            TransferJobStatus.Failed,
            0,
            100,
            "failed",
            1);
        viewModel.QueueItems.Add(new QueueItemView { Snapshot = queuedUpload });
        viewModel.QueueItems.Add(new QueueItemView { Snapshot = failedDownload });
        #endregion

        #region Initial Assert
        Assert.Equal(2, viewModel.QueueItemsView.Cast<object>().Count());
        #endregion

        #region Act
        viewModel.SelectedQueueStatusFilter = nameof(TransferJobStatus.Queued);
        viewModel.SelectedQueueDirectionFilter = nameof(TransferDirection.Upload);
        var filteredCount = viewModel.QueueItemsView.Cast<object>().Count();
        viewModel.ShowAllQueueFiltersCommand.Execute(null);
        #endregion

        #region Assert
        Assert.Equal(1, filteredCount);
        Assert.Equal("All", viewModel.SelectedQueueStatusFilter);
        Assert.Equal("All", viewModel.SelectedQueueDirectionFilter);
        Assert.Equal(2, viewModel.QueueItemsView.Cast<object>().Count());
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SignIn_SortsSelectorsAlphabetically()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new UnsortedDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        #endregion

        #region Initial Assert
        Assert.Empty(viewModel.Subscriptions);
        #endregion

        #region Act
        await viewModel.SignInCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(["A Subscription", "M Subscription", "Z Subscription"], viewModel.Subscriptions.Select(x => x.Name).ToArray());
        Assert.Equal(["aaccount", "maccount", "zaccount"], viewModel.StorageAccounts.Select(x => x.Name).ToArray());
        Assert.Equal(["ashare", "mshare", "zshare"], viewModel.FileShares.Select(x => x.Name).ToArray());
        #endregion
    }

    private static MainViewModel CreateViewModel(IRemoteCapabilityService remoteCapabilityService, out SpyTransferQueueService queue)
    {
        queue = new SpyTransferQueueService();
        return CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            remoteCapabilityService,
            new StubRemoteActionPolicyService());
    }

    private static MainViewModel CreateViewModel(out SpyTransferQueueService queue) =>
        CreateViewModel(new StubRemoteCapabilityService(), out queue);

    private static MainViewModel CreateViewModelWithDependencies(
        IAuthenticationService authenticationService,
        IAzureDiscoveryService discoveryService,
        ILocalBrowserService localBrowserService,
        IAzureFilesBrowserService azureBrowserService,
        ILocalFileOperationsService localFileOperationsService,
        IRemoteFileOperationsService remoteFileOperationsService,
        ITransferConflictProbeService transferConflictProbeService,
        IConflictResolutionPromptService conflictResolutionPromptService,
        SpyTransferQueueService queue,
        IMirrorPlannerService mirrorPlannerService,
        IMirrorExecutionService mirrorExecutionService,
        IConnectionProfileStore profileStore,
        IRemoteCapabilityService remoteCapabilityService,
        IRemoteActionPolicyService remoteActionPolicyService,
        IAppUpdateService? appUpdateService = null) =>
        new(
            authenticationService,
            discoveryService,
            localBrowserService,
            azureBrowserService,
            localFileOperationsService,
            remoteFileOperationsService,
            transferConflictProbeService,
            conflictResolutionPromptService,
            queue,
            mirrorPlannerService,
            mirrorExecutionService,
            profileStore,
            remoteCapabilityService,
            remoteActionPolicyService,
            appUpdateService ?? new StubAppUpdateService(new UpdateCheckResult("1.0.0", "1.0.0", false, null, "Up to date.", null)),
            new StubUserHelpContentService());

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
        public List<TransferRequest> EnqueuedRequests { get; } = [];
        public int PauseAllCount { get; private set; }

        public Guid Enqueue(TransferRequest request, bool startImmediately = true)
        {
            return EnqueueOrGetExisting(request, startImmediately).JobId;
        }

        public EnqueueResult EnqueueOrGetExisting(TransferRequest request, bool startImmediately = true)
        {
            var id = Guid.NewGuid();
            _ = JobUpdated;
            EnqueuedRequests.Add(request);
            return new EnqueueResult(id, AddedNew: true, TransferJobStatus.Queued);
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

        public Task PauseAllAsync(CancellationToken cancellationToken)
        {
            PauseAllCount++;
            return Task.CompletedTask;
        }

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

    private sealed class UnsortedDiscoveryService : IAzureDiscoveryService
    {
        public async IAsyncEnumerable<SubscriptionItem> ListSubscriptionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new SubscriptionItem("sub-z", "Z Subscription");
            yield return new SubscriptionItem("sub-a", "A Subscription");
            yield return new SubscriptionItem("sub-m", "M Subscription");
        }

        public async IAsyncEnumerable<StorageAccountItem> ListStorageAccountsAsync(string subscriptionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new StorageAccountItem(subscriptionId, "zaccount", "rg-z");
            yield return new StorageAccountItem(subscriptionId, "aaccount", "rg-a");
            yield return new StorageAccountItem(subscriptionId, "maccount", "rg-m");
        }

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new FileShareItem("zshare");
            yield return new FileShareItem("ashare");
            yield return new FileShareItem("mshare");
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

    private sealed class StubTransferConflictProbeService : ITransferConflictProbeService
    {
        public Task<bool> HasConflictAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<(string LocalPath, SharePath RemotePath)> ResolveRenameTargetAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken) =>
            Task.FromResult((localPath, remotePath));
    }

    private sealed class AlwaysConflictProbeService : ITransferConflictProbeService
    {
        public Task<bool> HasConflictAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<(string LocalPath, SharePath RemotePath)> ResolveRenameTargetAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken) =>
            Task.FromResult(($"{Path.GetFileNameWithoutExtension(localPath)} (1){Path.GetExtension(localPath)}", remotePath));
    }

    private sealed class StubConflictResolutionPromptService : IConflictResolutionPromptService
    {
        private readonly ConflictPromptAction _action;
        private readonly bool _doForAll;
        private readonly bool _returnsResult;

        public StubConflictResolutionPromptService(ConflictPromptAction action, bool doForAll, bool returnsResult)
        {
            _action = action;
            _doForAll = doForAll;
            _returnsResult = returnsResult;
        }

        public bool TryResolveConflict(TransferDirection direction, string sourcePath, string destinationPath, out ConflictPromptAction action, out bool doForAll)
        {
            action = _action;
            doForAll = _doForAll;
            return _returnsResult;
        }
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

    private sealed class StubAppUpdateService : IAppUpdateService
    {
        private readonly UpdateCheckResult _checkResult;

        public StubAppUpdateService(UpdateCheckResult checkResult)
        {
            _checkResult = checkResult;
        }

        public int CheckCount { get; private set; }
        public UpdateChannel CurrentChannel { get; private set; } = UpdateChannel.Stable;

        public void SetChannel(UpdateChannel channel)
        {
            CurrentChannel = channel;
        }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            return Task.FromResult(_checkResult);
        }

        public Task<UpdateDownloadResult> DownloadUpdateAsync(UpdateCandidate candidate, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateDownloadResult(candidate, @"C:\temp\update.msix", "abc", "abc", DateTimeOffset.UtcNow));

        public Task<UpdateValidationResult> ValidateDownloadedUpdateAsync(UpdateDownloadResult downloaded, CancellationToken cancellationToken) =>
            Task.FromResult(new UpdateValidationResult(true, "CN=Danm@de Software", downloaded.Candidate.Version + ".0", null));

        public Task LaunchInstallerAsync(UpdateDownloadResult downloaded, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubUserHelpContentService : IUserHelpContentService
    {
        public IReadOnlyList<HelpTopic> GetTopics() =>
        [
            new HelpTopic("overview", "Overview", "README.md")
        ];

        public Task<HelpDocument> LoadTopicAsync(string topicId, CancellationToken cancellationToken) =>
            Task.FromResult(new HelpDocument(topicId, "Overview", "# Help", "<html><body><h1>Help</h1></body></html>", "README.md"));
    }
}
