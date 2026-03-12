using Azure;
using Azure.Core;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;
using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.Models;
using AzureFilesSync.Desktop.Services;
using AzureFilesSync.Desktop.ViewModels;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace AzureFilesSync.UITests;

public sealed class MainWindowAndViewModelUiTests
{
    [Fact]
    public async Task MainWindow_RefocusPrompt_WhenNoPendingRemoteEditChanges_DoesNotSyncOrDiscard()
    {
        #region Arrange
        var remoteEdit = new StubRemoteEditSessionService();
        #endregion

        #region Act
        await RunInStaAsync(async () =>
        {
            var viewModel = CreateViewModelWithDependencies(
                new StubAuthenticationService(),
                new StubDiscoveryService(),
                null,
                new StubLocalBrowserService(),
                new StubAzureBrowserService(),
                new StubLocalFileOperationsService(),
                new StubRemoteFileOperationsService(),
                new StubTransferConflictProbeService(),
                new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
                new SpyTransferQueueService(),
                new StubMirrorPlannerService(),
                new StubMirrorExecutionService(),
                new InMemoryConnectionProfileStore(),
                new StubRemoteCapabilityService(),
                new StubRemoteActionPolicyService(),
                remoteEditSessionService: remoteEdit);

            var window = new AzureFilesSync.Desktop.MainWindow(viewModel);
            try
            {
                var method = typeof(AzureFilesSync.Desktop.MainWindow)
                    .GetMethod("PromptForRemoteEditChangesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var task = (Task)method.Invoke(window, null)!;
                await task;
            }
            finally
            {
                window.Close();
            }
        });
        #endregion

        #region Assert
        Assert.Equal(1, remoteEdit.PendingQueryCount);
        Assert.Empty(remoteEdit.SyncCalls);
        Assert.Empty(remoteEdit.DiscardCalls);
        #endregion
    }

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
    public void MainViewModel_FailedUploadPermissionMismatch_DisablesFurtherUploads_ButKeepsRemoteBrowsable()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
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
            new RemoteActionPolicyService());
        viewModel.SelectedSubscription = new SubscriptionItem("sub", "Subscription");
        viewModel.SelectedStorageAccount = new StorageAccountItem("sub", "smarthorizd5090cce0a", "rg");
        viewModel.SelectedFileShare = new FileShareItem("smarthorizons-site");
        viewModel.SelectedLocalEntry = new LocalEntry("local.txt", @"C:\work\local.txt", false, 10, DateTimeOffset.UtcNow);
        viewModel.SelectedRemoteEntry = new RemoteEntry("remote.txt", "remote.txt", false, 20, DateTimeOffset.UtcNow);

        var failedUpload = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(
                TransferDirection.Upload,
                @"C:\work\local.txt",
                new SharePath("smarthorizd5090cce0a", "smarthorizons-site", "public/_email/sh_institute/2026/local.txt")),
            TransferJobStatus.Failed,
            0,
            10,
            "This request is not authorized to perform this operation using this permission. ErrorCode: AuthorizationPermissionMismatch",
            0);
        #endregion

        #region Initial Assert
        Assert.True(viewModel.EnqueueUploadCommand.CanExecute(null));
        #endregion

        #region Act
        queue.RaiseJobUpdated(failedUpload);
        #endregion

        #region Assert
        Assert.Contains("does not have Azure Files upload permission", viewModel.RemotePaneStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.EnqueueUploadCommand.CanExecute(null));
        Assert.True(viewModel.EnqueueDownloadCommand.CanExecute(null));
        Assert.True(viewModel.RemoteCapability?.CanBrowse);
        #endregion
    }

    [Fact]
    public void MainViewModel_FailedUploadPermissionMismatch_FromDifferentRemoteContext_DoesNotChangeCurrentCapability()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
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
            new RemoteActionPolicyService());
        viewModel.SelectedSubscription = new SubscriptionItem("sub-b", "Subscription B");
        viewModel.SelectedStorageAccount = new StorageAccountItem("sub-b", "storage-b", "rg-b");
        viewModel.SelectedFileShare = new FileShareItem("share-b");
        viewModel.SelectedLocalEntry = new LocalEntry("local.txt", @"C:\work\local.txt", false, 10, DateTimeOffset.UtcNow);
        viewModel.SelectedRemoteEntry = new RemoteEntry("remote.txt", "remote.txt", false, 20, DateTimeOffset.UtcNow);

        var baselineCapability = new RemoteCapabilitySnapshot(
            RemoteAccessState.Accessible,
            CanBrowse: true,
            CanUpload: true,
            CanDownload: true,
            CanPlanMirror: true,
            CanExecuteMirror: true,
            UserMessage: string.Empty,
            EvaluatedUtc: DateTimeOffset.UtcNow);
        var failedUpload = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(
                TransferDirection.Upload,
                @"C:\work\local.txt",
                new SharePath("storage-a", "share-a", "public/_email/sh_institute/2026/local.txt")),
            TransferJobStatus.Failed,
            0,
            10,
            "This request is not authorized to perform this operation using this permission. ErrorCode: AuthorizationPermissionMismatch",
            0);
        #endregion

        #region Initial Assert
        ApplyCapability(viewModel, baselineCapability);
        Assert.Equal(string.Empty, viewModel.RemotePaneStatusMessage);
        Assert.True(viewModel.EnqueueUploadCommand.CanExecute(null));
        Assert.True(viewModel.EnqueueDownloadCommand.CanExecute(null));
        #endregion

        #region Act
        queue.RaiseJobUpdated(failedUpload);
        #endregion

        #region Assert
        Assert.Equal(baselineCapability, viewModel.RemoteCapability);
        Assert.Equal(string.Empty, viewModel.RemotePaneStatusMessage);
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
    public async Task MainViewModel_EnqueueDownload_UsesAllSelectedRemoteEntries()
    {
        #region Arrange
        var viewModel = CreateViewModel(out var queue);
        SetValidRemoteSelection(viewModel);
        var first = new RemoteEntry("a.txt", "a.txt", false, 10, DateTimeOffset.UtcNow);
        var second = new RemoteEntry("b.txt", "b.txt", false, 20, DateTimeOffset.UtcNow);
        viewModel.SelectedRemoteEntry = first;
        viewModel.UpdateSelectedRemoteSelection(new ArrayList { first, second });
        #endregion

        #region Initial Assert
        Assert.True(viewModel.EnqueueDownloadCommand.CanExecute(null));
        Assert.Empty(queue.EnqueuedRequests);
        #endregion

        #region Act
        await viewModel.EnqueueDownloadCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(2, queue.EnqueuedRequests.Count);
        Assert.Contains(queue.EnqueuedRequests, x => string.Equals(x.RemotePath.NormalizeRelativePath(), "a.txt", StringComparison.Ordinal));
        Assert.Contains(queue.EnqueuedRequests, x => string.Equals(x.RemotePath.NormalizeRelativePath(), "b.txt", StringComparison.Ordinal));
        #endregion
    }

    [Fact]
    public async Task MainViewModel_DeleteLocalSelection_SingleItem_DeletesAndSummarizes()
    {
        #region Arrange
        var localOps = new RecordingLocalFileOperationsService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            localOps,
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        var selected = new ArrayList
        {
            new LocalEntry("one.txt", @"C:\tmp\one.txt", false, 12, DateTimeOffset.UtcNow)
        };
        #endregion

        #region Initial Assert
        Assert.Empty(localOps.DeletedPaths);
        #endregion

        #region Act
        var result = await viewModel.DeleteLocalSelectionAsync(selected, recursive: true);
        #endregion

        #region Assert
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal([@"C:\tmp\one.txt"], localOps.DeletedPaths);
        Assert.Equal("Deleted 1 local item(s).", viewModel.StatusQueueText);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_DeleteLocalSelection_MultiSelect_IgnoresParentAndDeletesAll()
    {
        #region Arrange
        var localOps = new RecordingLocalFileOperationsService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            localOps,
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        var selected = new ArrayList
        {
            new LocalEntry("..", @"C:\tmp", true, 0, DateTimeOffset.UtcNow),
            new LocalEntry("one.txt", @"C:\tmp\one.txt", false, 12, DateTimeOffset.UtcNow),
            new LocalEntry("two.txt", @"C:\tmp\two.txt", false, 24, DateTimeOffset.UtcNow)
        };
        #endregion

        #region Initial Assert
        Assert.Empty(localOps.DeletedPaths);
        #endregion

        #region Act
        var result = await viewModel.DeleteLocalSelectionAsync(selected, recursive: true);
        #endregion

        #region Assert
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, localOps.DeletedPaths.Count);
        Assert.Contains(@"C:\tmp\one.txt", localOps.DeletedPaths);
        Assert.Contains(@"C:\tmp\two.txt", localOps.DeletedPaths);
        Assert.Equal("Deleted 2 local item(s).", viewModel.StatusQueueText);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_DeleteRemoteSelection_MultiSelect_SummarizesFailures()
    {
        #region Arrange
        var remoteOps = new RecordingRemoteFileOperationsService(["bad.txt"]);
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            remoteOps,
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);

        var selected = new ArrayList
        {
            new RemoteEntry("..", string.Empty, true, 0, DateTimeOffset.UtcNow),
            new RemoteEntry("ok.txt", "ok.txt", false, 1, DateTimeOffset.UtcNow),
            new RemoteEntry("bad.txt", "bad.txt", false, 1, DateTimeOffset.UtcNow)
        };
        #endregion

        #region Initial Assert
        Assert.Empty(remoteOps.DeleteAttempts);
        #endregion

        #region Act
        var result = await viewModel.DeleteRemoteSelectionAsync(selected, recursive: true);
        #endregion

        #region Assert
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, remoteOps.DeleteAttempts.Count);
        Assert.Contains("ok.txt", remoteOps.DeleteAttempts.Select(x => x.NormalizeRelativePath()));
        Assert.Contains("bad.txt", remoteOps.DeleteAttempts.Select(x => x.NormalizeRelativePath()));
        Assert.Equal("Deleted 1 of 2 remote item(s). Failed: 1.", viewModel.StatusQueueText);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_LoadRemoteDirectory_UsesPaging_AndLoadMoreAppendsEntries()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        var rootEntries = new List<RemoteEntry>
        {
            new("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)
        };
        var nextEntries = new List<RemoteEntry>
        {
            new("file-b.txt", "file-b.txt", false, 15, DateTimeOffset.UtcNow)
        };
        browser.ListDirectoryPageBehavior = (path, continuation, _) =>
            continuation is null
                ? new RemoteDirectoryPage(rootEntries, "page-2", true)
                : new RemoteDirectoryPage(nextEntries, null, false);

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        #endregion

        #region Act
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        await viewModel.LoadMoreRemoteEntriesCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(2, viewModel.RemoteEntries.Count);
        Assert.Contains(viewModel.RemoteEntries, x => x.Name == "folder-a");
        Assert.Contains(viewModel.RemoteEntries, x => x.Name == "file-b.txt");
        Assert.False(viewModel.HasMoreRemoteEntries);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SearchRemote_StreamsResultsBeforeCompletion_AndClearRestoresBrowseView()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
            string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
                ? new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false)
                : new RemoteDirectoryPage([], null, false);

        var firstChunkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new StubRemoteSearchService
        {
            SearchIncrementalBehavior = (_, token) => StreamAsync(token)
        };

        async IAsyncEnumerable<RemoteSearchProgress> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return new RemoteSearchProgress(
                [new RemoteEntry("file-a.log", "folder-a/file-a.log", false, 12, DateTimeOffset.UtcNow)],
                TotalMatches: 1,
                IsCompleted: false,
                IsTruncated: false,
                ScannedDirectories: 1,
                ScannedEntries: 1);

            firstChunkReady.TrySetResult(true);
            await continueSearch.Task.WaitAsync(token);

            yield return new RemoteSearchProgress(
                [new RemoteEntry("file-b.log", "folder-a/file-b.log", false, 15, DateTimeOffset.UtcNow)],
                TotalMatches: 2,
                IsCompleted: true,
                IsTruncated: false,
                ScannedDirectories: 1,
                ScannedEntries: 2);
        }

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteSearchService: search);
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        viewModel.RemoteSearchQuery = "file";
        #endregion

        #region Initial Assert
        Assert.False(viewModel.IsRemoteSearchActive);
        Assert.Single(viewModel.RemoteEntries);
        #endregion

        #region Act
        var runTask = viewModel.SearchRemoteCommand.ExecuteAsync(null);
        await firstChunkReady.Task;
        #endregion

        #region Assert
        Assert.True(viewModel.IsRemoteSearchActive);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Contains("Searching", viewModel.RemoteSearchStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(viewModel.RemoteGridEntries, x => x.Name == "..");
        Assert.False(viewModel.LoadMoreRemoteEntriesCommand.CanExecute(null));

        continueSearch.TrySetResult(true);
        await runTask;

        Assert.Equal(2, viewModel.RemoteEntries.Count);
        Assert.Contains("Found 2 match(es)", viewModel.RemoteSearchStatusMessage, StringComparison.OrdinalIgnoreCase);
        await viewModel.ClearRemoteSearchCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRemoteSearchActive);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Contains(viewModel.RemoteGridEntries, x => x.Name == "folder-a");
        #endregion
    }

    [Fact]
    public async Task MainViewModel_CancelRemoteSearch_KeepsPartialResults_AndSetsCanceledStatus()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
            string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
                ? new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false)
                : new RemoteDirectoryPage([], null, false);

        var firstChunkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new StubRemoteSearchService
        {
            SearchIncrementalBehavior = (_, token) => StreamAsync(token)
        };

        async IAsyncEnumerable<RemoteSearchProgress> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return new RemoteSearchProgress(
                [new RemoteEntry("file-a.log", "folder-a/file-a.log", false, 12, DateTimeOffset.UtcNow)],
                TotalMatches: 1,
                IsCompleted: false,
                IsTruncated: false,
                ScannedDirectories: 1,
                ScannedEntries: 1);

            firstChunkReady.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            yield return new RemoteSearchProgress([], 1, IsCompleted: true, IsTruncated: false, 1, 1);
        }

        var scheduler = new CancelAwareRemoteReadTaskScheduler();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteSearchService: search,
            remoteReadTaskScheduler: scheduler);
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        viewModel.RemoteSearchQuery = "file";
        #endregion

        #region Initial Assert
        Assert.False(viewModel.IsRemoteLoading);
        #endregion

        #region Act
        var runTask = viewModel.SearchRemoteCommand.ExecuteAsync(null);
        await firstChunkReady.Task;
        Assert.True(viewModel.CancelRemoteSearchCommand.CanExecute(null));
        await viewModel.CancelRemoteSearchCommand.ExecuteAsync(null);
        await runTask;
        #endregion

        #region Assert
        Assert.True(viewModel.IsRemoteSearchActive);
        Assert.False(viewModel.IsRemoteLoading);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Contains("canceled", viewModel.RemoteSearchStatusMessage, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_CancelThenRestartSearch_UsesLatestRunResults()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
            string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
                ? new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false)
                : new RemoteDirectoryPage([], null, false);

        var firstChunkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchInvocation = 0;
        var search = new StubRemoteSearchService
        {
            SearchIncrementalBehavior = (_, token) =>
            {
                var invocation = System.Threading.Interlocked.Increment(ref searchInvocation);
                return invocation == 1 ? FirstRunAsync(token) : SecondRunAsync(token);
            }
        };

        async IAsyncEnumerable<RemoteSearchProgress> FirstRunAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return new RemoteSearchProgress(
                [new RemoteEntry("first-run.log", "folder-a/first-run.log", false, 12, DateTimeOffset.UtcNow)],
                TotalMatches: 1,
                IsCompleted: false,
                IsTruncated: false,
                ScannedDirectories: 1,
                ScannedEntries: 1);

            firstChunkReady.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            yield return new RemoteSearchProgress([], 1, IsCompleted: true, IsTruncated: false, 1, 1);
        }

        async IAsyncEnumerable<RemoteSearchProgress> SecondRunAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new RemoteSearchProgress(
                [new RemoteEntry("second-run.log", "folder-a/second-run.log", false, 24, DateTimeOffset.UtcNow)],
                TotalMatches: 1,
                IsCompleted: true,
                IsTruncated: false,
                ScannedDirectories: 1,
                ScannedEntries: 2);
        }

        var scheduler = new CancelAwareRemoteReadTaskScheduler();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteSearchService: search,
            remoteReadTaskScheduler: scheduler);
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        viewModel.RemoteSearchQuery = "run";
        #endregion

        #region Initial Assert
        Assert.False(viewModel.IsRemoteLoading);
        #endregion

        #region Act
        var firstRunTask = viewModel.SearchRemoteCommand.ExecuteAsync(null);
        await firstChunkReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.CancelRemoteSearchCommand.ExecuteAsync(null);
        await firstRunTask;

        var secondRunTask = viewModel.SearchRemoteCommand.ExecuteAsync(null);
        await secondRunTask;
        #endregion

        #region Assert
        Assert.Equal(2, searchInvocation);
        Assert.False(viewModel.IsRemoteLoading);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("second-run.log", viewModel.RemoteEntries[0].Name);
        Assert.Contains("Found 1 match(es)", viewModel.RemoteSearchStatusMessage, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SearchRemote_IgnoresSecondInvocationWhileSearchIsActive()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
            string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
                ? new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false)
                : new RemoteDirectoryPage([], null, false);

        var firstChunkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        var search = new StubRemoteSearchService
        {
            SearchIncrementalBehavior = (_, token) => StreamAsync(token)
        };

        async IAsyncEnumerable<RemoteSearchProgress> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            System.Threading.Interlocked.Increment(ref invocationCount);
            yield return new RemoteSearchProgress([], 0, IsCompleted: false, IsTruncated: false, 1, 0);
            firstChunkReady.TrySetResult(true);
            await releaseSearch.Task.WaitAsync(token);
            yield return new RemoteSearchProgress(
                [new RemoteEntry("result.log", "folder-a/result.log", false, 10, DateTimeOffset.UtcNow)],
                TotalMatches: 1,
                IsCompleted: true,
                IsTruncated: false,
                ScannedDirectories: 1,
                ScannedEntries: 10);
        }

        var scheduler = new CancelAwareRemoteReadTaskScheduler();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteSearchService: search,
            remoteReadTaskScheduler: scheduler);
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        viewModel.RemoteSearchQuery = "result";
        #endregion

        #region Initial Assert
        Assert.False(viewModel.IsRemoteLoading);
        #endregion

        #region Act
        var firstRunTask = viewModel.SearchRemoteCommand.ExecuteAsync(null);
        await firstChunkReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.SearchRemoteCommand.ExecuteAsync(null);
        releaseSearch.TrySetResult(true);
        await firstRunTask;
        #endregion

        #region Assert
        Assert.Equal(1, invocationCount);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("result.log", viewModel.RemoteEntries[0].Name);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SearchRemote_ReconcilesFromSnapshot_WhenIncrementalMatchesMissing()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
            string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
                ? new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false)
                : new RemoteDirectoryPage([], null, false);

        var expected = new RemoteEntry("user.log", "folder-a/user.log", false, 10, DateTimeOffset.UtcNow);
        var search = new StubRemoteSearchService
        {
            SearchIncrementalBehavior = (_, _) =>
                Stream(
                [
                    new RemoteSearchProgress(
                        [],
                        TotalMatches: 1,
                        IsCompleted: false,
                        IsTruncated: false,
                        ScannedDirectories: 1,
                        ScannedEntries: 10,
                        SnapshotMatches: [expected]),
                    new RemoteSearchProgress(
                        [],
                        TotalMatches: 1,
                        IsCompleted: true,
                        IsTruncated: false,
                        ScannedDirectories: 1,
                        ScannedEntries: 10,
                        SnapshotMatches: [expected])
                ])
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteSearchService: search);
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        viewModel.RemoteSearchQuery = "user";
        #endregion

        #region Initial Assert
        Assert.False(viewModel.IsRemoteLoading);
        #endregion

        #region Act
        await viewModel.SearchRemoteCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal(expected.FullPath, viewModel.RemoteEntries[0].FullPath);
        Assert.Contains("Found 1 match(es)", viewModel.RemoteSearchStatusMessage, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_OpenRemoteEntry_WhenSearchActive_NavigatesToParentAndSelectsMatch()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
        {
            var normalized = path.NormalizeRelativePath();
            return string.Equals(normalized, "folder-a", StringComparison.OrdinalIgnoreCase)
                ? new RemoteDirectoryPage(
                    [new RemoteEntry("file-a.log", "folder-a/file-a.log", false, 12, DateTimeOffset.UtcNow)],
                    null,
                    false)
                : new RemoteDirectoryPage([], null, false);
        };

        var search = new StubRemoteSearchService
        {
            SearchIncrementalBehavior = (_, _) =>
                Stream(
                [
                    new RemoteSearchProgress(
                        [new RemoteEntry("file-a.log", "folder-a/file-a.log", false, 12, DateTimeOffset.UtcNow)],
                        TotalMatches: 1,
                        IsCompleted: true,
                        IsTruncated: false,
                        ScannedDirectories: 1,
                        ScannedEntries: 1)
                ])
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteSearchService: search);
        SetValidRemoteSelection(viewModel);
        viewModel.RemoteSearchQuery = "file-a";
        await viewModel.SearchRemoteCommand.ExecuteAsync(null);
        var result = Assert.Single(viewModel.RemoteEntries);
        #endregion

        #region Initial Assert
        Assert.True(viewModel.IsRemoteSearchActive);
        Assert.Equal("folder-a/file-a.log", result.FullPath);
        #endregion

        #region Act
        await viewModel.OpenRemoteEntryAsync(result);
        #endregion

        #region Assert
        Assert.False(viewModel.IsRemoteSearchActive);
        Assert.Equal("folder-a", viewModel.RemotePath);
        Assert.NotNull(viewModel.SelectedRemoteEntry);
        Assert.Equal("folder-a/file-a.log", viewModel.SelectedRemoteEntry!.FullPath);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_OpenRemoteEntry_Failure_RestoresPreviousView()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageBehavior = (path, _, _) =>
        {
            if (string.IsNullOrWhiteSpace(path.NormalizeRelativePath()))
            {
                return new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false);
            }

            throw new InvalidOperationException("Simulated remote failure.");
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        var directory = Assert.Single(viewModel.RemoteEntries);
        #endregion

        #region Initial Assert
        Assert.Equal(string.Empty, viewModel.RemotePath);
        Assert.Equal("folder-a", directory.Name);
        #endregion

        #region Act
        await viewModel.OpenRemoteEntryAsync(directory);
        #endregion

        #region Assert
        Assert.Equal(string.Empty, viewModel.RemotePath);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("folder-a", viewModel.RemoteEntries[0].Name);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_OpenRemoteEntry_WhenLoadTimesOut_RestoresPreviousView()
    {
        #region Arrange
        var browser = new StubAzureBrowserService();
        browser.ListDirectoryPageAsyncBehavior = async (path, _, _, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(path.NormalizeRelativePath()))
            {
                return new RemoteDirectoryPage(
                    [new RemoteEntry("folder-a", "folder-a", true, 0, DateTimeOffset.UtcNow)],
                    null,
                    false);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new RemoteDirectoryPage([], null, false);
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService(),
            remoteOpenDirectoryTimeout: TimeSpan.FromMilliseconds(50));
        SetValidRemoteSelection(viewModel);
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        var directory = Assert.Single(viewModel.RemoteEntries);
        #endregion

        #region Initial Assert
        Assert.Equal(string.Empty, viewModel.RemotePath);
        Assert.Equal("folder-a", directory.Name);
        #endregion

        #region Act
        await viewModel.OpenRemoteEntryAsync(directory);
        #endregion

        #region Assert
        Assert.Equal(string.Empty, viewModel.RemotePath);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("folder-a", viewModel.RemoteEntries[0].Name);
        Assert.Contains("timed out", viewModel.StatusQueueText, StringComparison.OrdinalIgnoreCase);
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
    public async Task MainViewModel_ClearCompletedCanceledQueue_PurgesAndRefreshesQueue()
    {
        #region Arrange
        var viewModel = CreateViewModel(out var queue);
        var completed = new TransferJobSnapshot(
            Guid.NewGuid(),
            new TransferRequest(TransferDirection.Upload, @"C:\tmp\a.txt", new SharePath("acct", "share", "a.txt")),
            TransferJobStatus.Completed,
            100,
            100,
            "Completed",
            0);
        var canceled = completed with { JobId = Guid.NewGuid(), Status = TransferJobStatus.Canceled, Message = "Canceled" };
        var running = completed with { JobId = Guid.NewGuid(), Status = TransferJobStatus.Running, Message = null };
        queue.SnapshotItems.AddRange([completed, canceled, running]);
        viewModel.QueueItems.Add(new QueueItemView { Snapshot = completed });
        viewModel.QueueItems.Add(new QueueItemView { Snapshot = canceled });
        viewModel.QueueItems.Add(new QueueItemView { Snapshot = running });
        #endregion

        #region Initial Assert
        Assert.True(viewModel.ClearCompletedCanceledQueueCommand.CanExecute(null));
        Assert.Equal(3, viewModel.QueueItems.Count);
        #endregion

        #region Act
        await viewModel.ClearCompletedCanceledQueueCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Single(queue.PurgeCalls);
        Assert.Contains(TransferJobStatus.Completed, queue.PurgeCalls[0]);
        Assert.Contains(TransferJobStatus.Canceled, queue.PurgeCalls[0]);
        Assert.Single(viewModel.QueueItems);
        Assert.Equal(TransferJobStatus.Running, viewModel.QueueItems[0].Status);
        Assert.False(viewModel.ClearCompletedCanceledQueueCommand.CanExecute(null));
        #endregion
    }

    [Fact]
    public async Task MainViewModel_LoadRemoteDirectory_NotFoundPath_FallsBackToRoot()
    {
        #region Arrange
        var browser = new StubAzureBrowserService
        {
            ListDirectoryPageBehavior = (path, _, _) =>
            {
                var normalized = path.NormalizeRelativePath();
                return string.IsNullOrWhiteSpace(normalized)
                    ? new RemoteDirectoryPage(
                        [new RemoteEntry("root-folder", "root-folder", true, 0, DateTimeOffset.UtcNow)],
                        null,
                        false)
                    : throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
            }
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new PathAwareRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.RemotePath = "missing";
        #endregion

        #region Initial Assert
        Assert.Equal("missing", viewModel.RemotePath);
        #endregion

        #region Act
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(string.Empty, viewModel.RemotePath);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("root-folder", viewModel.RemoteEntries[0].Name);
        Assert.Equal(string.Empty, viewModel.RemotePaneStatusMessage);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_LoadRemoteDirectory_NotFoundTargetAndRoot_RollsBackToLastSuccessfulPath()
    {
        #region Arrange
        var browser = new StubAzureBrowserService
        {
            ListDirectoryPageBehavior = (path, _, _) =>
            {
                var normalized = path.NormalizeRelativePath();
                if (string.Equals(normalized, "known", StringComparison.OrdinalIgnoreCase))
                {
                    return new RemoteDirectoryPage(
                        [new RemoteEntry("known-file.txt", "known/known-file.txt", false, 10, DateTimeOffset.UtcNow)],
                        null,
                        false);
                }

                throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
            }
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new PathStateRemoteCapabilityService("known"),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.RemotePath = "known";
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        #endregion

        #region Initial Assert
        Assert.Equal("known", viewModel.RemotePath);
        #endregion

        #region Act
        viewModel.RemotePath = "missing";
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal("known", viewModel.RemotePath);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("known-file.txt", viewModel.RemoteEntries[0].Name);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_LoadRemoteDirectory_BlobMissingVirtualPath_FallsBackToRoot()
    {
        #region Arrange
        var browser = new StubAzureBrowserService
        {
            ListDirectoryPageBehavior = (path, _, _) =>
            {
                var normalized = path.NormalizeRelativePath();
                return string.IsNullOrWhiteSpace(normalized)
                    ? new RemoteDirectoryPage(
                        [new RemoteEntry("root-blob.txt", "root-blob.txt", false, 10, DateTimeOffset.UtcNow)],
                        null,
                        false)
                    : new RemoteDirectoryPage([], null, false);
            },
            GetEntryDetailsAsyncBehavior = (path, _) =>
            {
                var normalized = path.NormalizeRelativePath();
                return Task.FromResult<RemoteEntry?>(
                    string.Equals(normalized, "courseware", StringComparison.Ordinal)
                        ? null
                        : new RemoteEntry("entry", normalized, true, 0, DateTimeOffset.UtcNow));
            }
        };

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            browser,
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        viewModel.SelectedSubscription = new SubscriptionItem("sub", "Subscription");
        viewModel.SelectedStorageAccount = new StorageAccountItem("sub", "storage", "rg");
        viewModel.SelectedFileShare = new FileShareItem("container", RemoteRootKind.BlobContainer);
        viewModel.RemotePath = "courseware";
        #endregion

        #region Initial Assert
        Assert.Equal(RemoteProviderKind.AzureBlob, viewModel.SelectedFileShare.ProviderKind);
        Assert.Equal("courseware", viewModel.RemotePath);
        #endregion

        #region Act
        await viewModel.LoadRemoteDirectoryCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(string.Empty, viewModel.RemotePath);
        Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("root-blob.txt", viewModel.RemoteEntries[0].Name);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_CreateLocalFolder_WhenNewFolderExists_UsesIncrementedName()
    {
        #region Arrange
        var localOps = new StubLocalFileOperationsService();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"storage-zilla-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(Path.Combine(tempRoot, "New Folder"));
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            localOps,
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        viewModel.LocalPath = tempRoot;
        #endregion

        #region Initial Assert
        Assert.True(viewModel.CreateLocalFolderCommand.CanExecute(null));
        #endregion

        #region Act
        await viewModel.CreateLocalFolderCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Equal(tempRoot, localOps.LastCreateParentPath);
        Assert.Equal("New Folder (1)", localOps.LastCreateName);
        #endregion

        #region Cleanup
        Directory.Delete(tempRoot, recursive: true);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_CreateRemoteFolder_WhenNewFolderExists_UsesIncrementedName()
    {
        #region Arrange
        var remoteOps = new StubRemoteFileOperationsService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            remoteOps,
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            new SpyTransferQueueService(),
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            new InMemoryConnectionProfileStore(),
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        SetValidRemoteSelection(viewModel);
        viewModel.RemotePath = "parent";
        viewModel.RemoteEntries.Add(new RemoteEntry("New Folder", "parent/New Folder", true, 0, DateTimeOffset.UtcNow));
        #endregion

        #region Initial Assert
        Assert.True(viewModel.CreateRemoteFolderCommand.CanExecute(null));
        #endregion

        #region Act
        await viewModel.CreateRemoteFolderCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.NotNull(remoteOps.LastCreatePath);
        Assert.Equal("parent/New Folder (1)", remoteOps.LastCreatePath!.NormalizeRelativePath());
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
            null,
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
            null,
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
            null,
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
            null,
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
            null,
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

    [Fact]
    public async Task MainViewModel_SwitchingSubscription_LoadsNewStorageAccounts()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new MultiSubscriptionDiscoveryService(),
            null,
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
            new RemoteActionPolicyService());
        #endregion

        #region Initial Assert
        await viewModel.SignInCommand.ExecuteAsync(null);
        Assert.Equal(["alpha-a", "alpha-b"], viewModel.StorageAccounts.Select(x => x.Name).ToArray());
        #endregion

        #region Act
        viewModel.SelectedSubscription = viewModel.Subscriptions.Single(x => x.Id == "sub-beta");
        await WaitUntilAsync(() => !viewModel.IsRemoteLoading, TimeSpan.FromSeconds(5));
        #endregion

        #region Assert
        Assert.Equal(["beta-a", "beta-b"], viewModel.StorageAccounts.Select(x => x.Name).ToArray());
        Assert.Equal("beta-a", viewModel.SelectedStorageAccount?.Name);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SignIn_WithBrowserFallback_ShowsFallbackStatus()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(new LoginSession(true, "tester", "tenant", "SystemBrowser", UsedFallback: true)),
            new StubDiscoveryService(),
            null,
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
        Assert.Equal("Not signed in", viewModel.LoginStatus);
        #endregion

        #region Act
        await viewModel.SignInCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Contains("browser fallback", viewModel.LoginStatus, StringComparison.OrdinalIgnoreCase);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SignIn_LegacyProfileWithoutRootKind_PrefersFileShareWhenNamesOverlap()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var defaultLocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profileStore = new ReadOnlyConnectionProfileStore(
            new ConnectionProfile(
                SubscriptionId: "sub",
                StorageAccountName: "storage",
                FileShareName: "shared-root",
                LocalPath: defaultLocalPath,
                RemotePath: string.Empty,
                IncludeDeletes: false,
                TransferMaxConcurrency: 4,
                TransferMaxBytesPerSecond: 0,
                UploadConflictDefaultPolicy: TransferConflictPolicy.Ask,
                DownloadConflictDefaultPolicy: TransferConflictPolicy.Ask,
                RecentLocalPaths: [defaultLocalPath],
                RecentRemotePaths: [],
                LocalGridLayout: null,
                RemoteGridLayout: null,
                UpdateChannel: UpdateChannel.Stable,
                RemoteRootKind: null));

        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new OverlappingRootDiscoveryService(),
            null,
            new StubLocalBrowserService(),
            new StubAzureBrowserService(),
            new StubLocalFileOperationsService(),
            new StubRemoteFileOperationsService(),
            new StubTransferConflictProbeService(),
            new StubConflictResolutionPromptService(ConflictPromptAction.Skip, false, returnsResult: true),
            queue,
            new StubMirrorPlannerService(),
            new StubMirrorExecutionService(),
            profileStore,
            new StubRemoteCapabilityService(),
            new StubRemoteActionPolicyService());
        #endregion

        #region Initial Assert
        Assert.Null(viewModel.SelectedFileShare);
        #endregion

        #region Act
        await viewModel.SignInCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        var selected = Assert.IsType<FileShareItem>(viewModel.SelectedFileShare);
        Assert.Equal("shared-root", selected.Name);
        Assert.Equal(RemoteRootKind.FileShare, selected.Kind);
        #endregion
    }

    [Fact]
    public async Task MainViewModel_SignIn_WhenShareEndpointDnsFails_KeepsSignedInAndShowsFriendlyRemoteMessage()
    {
        #region Arrange
        var queue = new SpyTransferQueueService();
        var viewModel = CreateViewModelWithDependencies(
            new StubAuthenticationService(new LoginSession(true, "tester", "tenant")),
            new DnsFailingDiscoveryService(),
            null,
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
        Assert.Equal("Not signed in", viewModel.LoginStatus);
        #endregion

        #region Act
        await viewModel.SignInCommand.ExecuteAsync(null);
        #endregion

        #region Assert
        Assert.Contains("Signed in", viewModel.LoginStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unreachable", viewModel.LoginStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cannot resolve", viewModel.RemotePaneStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EndpointHost:", viewModel.RemoteDiagnosticsDetails, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CopyRemoteDiagnosticsCommand.CanExecute(null));
        #endregion
    }

    private static MainViewModel CreateViewModel(IRemoteCapabilityService remoteCapabilityService, out SpyTransferQueueService queue)
    {
        queue = new SpyTransferQueueService();
        return CreateViewModelWithDependencies(
            new StubAuthenticationService(),
            new StubDiscoveryService(),
            null,
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
        IStorageEndpointPreflightService? storageEndpointPreflightService,
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
        IAppUpdateService? appUpdateService = null,
        IRemoteSearchService? remoteSearchService = null,
        IRemoteReadTaskScheduler? remoteReadTaskScheduler = null,
        TimeSpan? remoteOpenDirectoryTimeout = null,
        IRemoteEditSessionService? remoteEditSessionService = null) =>
        new(
            authenticationService,
            discoveryService,
            storageEndpointPreflightService ?? new StubStorageEndpointPreflightService(),
            remoteReadTaskScheduler ?? new StubRemoteReadTaskScheduler(),
            localBrowserService,
            azureBrowserService,
            remoteSearchService ?? new StubRemoteSearchService(),
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
            new StubUserHelpContentService(),
            remoteOpenDirectoryTimeout,
            remoteEditSessionService: remoteEditSessionService);

    private static void SetValidRemoteSelection(MainViewModel viewModel)
    {
        viewModel.SelectedSubscription = new SubscriptionItem("sub", "Subscription");
        viewModel.SelectedStorageAccount = new StorageAccountItem("sub", "storage", "rg");
        viewModel.SelectedFileShare = new FileShareItem("share");
    }

    private static void ApplyCapability(MainViewModel viewModel, RemoteCapabilitySnapshot snapshot)
    {
        var method = typeof(MainViewModel).GetMethod("ApplyCapability", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(viewModel, [snapshot]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for condition.");
    }


    private static Task RunInStaAsync(Func<Task> work)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work().GetAwaiter().GetResult();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
    private sealed class SpyTransferQueueService : ITransferQueueService
    {
        public event EventHandler<TransferJobSnapshot>? JobUpdated;
        public List<Guid> PausedJobIds { get; } = [];
        public List<Guid> ResumedJobIds { get; } = [];
        public List<Guid> RetriedJobIds { get; } = [];
        public List<Guid> CanceledJobIds { get; } = [];
        public List<TransferRequest> EnqueuedRequests { get; } = [];
        public List<TransferJobSnapshot> SnapshotItems { get; } = [];
        public List<IReadOnlyCollection<TransferJobStatus>> PurgeCalls { get; } = [];
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

        public Task<int> PurgeAsync(IReadOnlyCollection<TransferJobStatus> statuses, CancellationToken cancellationToken)
        {
            PurgeCalls.Add(statuses);
            var removed = SnapshotItems.RemoveAll(x => statuses.Contains(x.Status));
            return Task.FromResult(removed);
        }

        public IReadOnlyList<TransferJobSnapshot> Snapshot() => SnapshotItems.ToList();

        public void RaiseJobUpdated(TransferJobSnapshot snapshot) => JobUpdated?.Invoke(this, snapshot);
    }

    private sealed class StubAuthenticationService : IAuthenticationService
    {
        private readonly LoginSession _session;

        public StubAuthenticationService(LoginSession? session = null)
        {
            _session = session ?? new LoginSession(true, "tester", "tenant");
        }

        public TokenCredential GetCredential() => throw new NotSupportedException();
        public Task<LoginSession> SignInInteractiveAsync(CancellationToken cancellationToken) => Task.FromResult(_session);
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

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, bool includeFileShares, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (includeFileShares)
            {
                yield return new FileShareItem("share");
            }
        }
    }

    private sealed class StubStorageEndpointPreflightService : IStorageEndpointPreflightService
    {
        public Task<(bool Success, string? FailureSummary)> ValidateAsync(string endpointHost, CancellationToken cancellationToken) =>
            Task.FromResult<(bool Success, string? FailureSummary)>((true, null));
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

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, bool includeFileShares, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (includeFileShares)
            {
                yield return new FileShareItem("zshare");
                yield return new FileShareItem("ashare");
                yield return new FileShareItem("mshare");
            }
        }
    }

    private sealed class MultiSubscriptionDiscoveryService : IAzureDiscoveryService
    {
        public async IAsyncEnumerable<SubscriptionItem> ListSubscriptionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new SubscriptionItem("sub-alpha", "Alpha");
            yield return new SubscriptionItem("sub-beta", "Beta");
        }

        public async IAsyncEnumerable<StorageAccountItem> ListStorageAccountsAsync(string subscriptionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            if (subscriptionId == "sub-alpha")
            {
                yield return new StorageAccountItem(subscriptionId, "alpha-b", "rg-alpha");
                yield return new StorageAccountItem(subscriptionId, "alpha-a", "rg-alpha");
                yield break;
            }

            yield return new StorageAccountItem(subscriptionId, "beta-b", "rg-beta");
            yield return new StorageAccountItem(subscriptionId, "beta-a", "rg-beta");
        }

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, bool includeFileShares, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (includeFileShares)
            {
                yield return new FileShareItem($"{storageAccountName}-share");
            }
        }
    }

    private sealed class OverlappingRootDiscoveryService : IAzureDiscoveryService
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

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, bool includeFileShares, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (includeFileShares)
            {
                yield return new FileShareItem("shared-root", RemoteRootKind.FileShare);
            }

            yield return new FileShareItem("shared-root", RemoteRootKind.BlobContainer);
        }
    }

    private sealed class DnsFailingDiscoveryService : IAzureDiscoveryService
    {
        public async IAsyncEnumerable<SubscriptionItem> ListSubscriptionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new SubscriptionItem("sub", "Subscription");
        }

        public async IAsyncEnumerable<StorageAccountItem> ListStorageAccountsAsync(string subscriptionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new StorageAccountItem(subscriptionId, "nexportstudiostorage", "rg");
        }

        public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, bool includeFileShares, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (includeFileShares)
            {
                var socket = new SocketException((int)SocketError.HostNotFound);
                var http = new HttpRequestException("No such host is known.", socket);
                throw new AggregateException(http);
            }

            yield return new FileShareItem("container-a", RemoteRootKind.BlobContainer);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
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
        public Func<SharePath, IReadOnlyList<RemoteEntry>> ListDirectoryBehavior { get; set; } = _ => [];
        public Func<SharePath, string?, int, RemoteDirectoryPage> ListDirectoryPageBehavior { get; set; } =
            (_, _, _) => new RemoteDirectoryPage([], null, false);
        public Func<SharePath, string?, int, CancellationToken, Task<RemoteDirectoryPage>>? ListDirectoryPageAsyncBehavior { get; set; }
        public Func<SharePath, CancellationToken, Task<RemoteEntry?>> GetEntryDetailsAsyncBehavior { get; set; } =
            (_, _) => Task.FromResult<RemoteEntry?>(null);

        public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken) =>
            Task.FromResult(ListDirectoryBehavior(path));

        public Task<RemoteDirectoryPage> ListDirectoryPageAsync(SharePath path, string? continuationToken, int pageSize, CancellationToken cancellationToken) =>
            ListDirectoryPageAsyncBehavior is not null
                ? ListDirectoryPageAsyncBehavior(path, continuationToken, pageSize, cancellationToken)
                : Task.FromResult(ListDirectoryPageBehavior(path, continuationToken, pageSize));

        public Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken) =>
            GetEntryDetailsAsyncBehavior(path, cancellationToken);
    }

    private sealed class StubRemoteSearchService : IRemoteSearchService
    {
        public Func<RemoteSearchRequest, CancellationToken, IAsyncEnumerable<RemoteSearchProgress>> SearchIncrementalBehavior { get; set; } =
            (_, _) => Stream([new RemoteSearchProgress([], 0, IsCompleted: true, IsTruncated: false, 0, 0)]);

        public IAsyncEnumerable<RemoteSearchProgress> SearchIncrementalAsync(RemoteSearchRequest request, CancellationToken cancellationToken) =>
            SearchIncrementalBehavior(request, cancellationToken);
    }


    private sealed class StubRemoteEditSessionService : IRemoteEditSessionService
    {
        public int PendingQueryCount { get; private set; }
        public List<Guid> DiscardCalls { get; } = [];
        public List<(Guid SessionId, bool Overwrite)> SyncCalls { get; } = [];
        public IReadOnlyList<RemoteEditPendingChange> PendingChanges { get; set; } = [];

        public Task<RemoteEditOpenResult> OpenAsync(SharePath remotePath, string displayName, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteEditOpenResult(Guid.NewGuid(), @"C:\\temp\\remote-edit.txt"));

        public Task<IReadOnlyList<RemoteEditPendingChange>> GetPendingChangesAsync(CancellationToken cancellationToken)
        {
            PendingQueryCount++;
            return Task.FromResult(PendingChanges);
        }

        public Task<RemoteEditSyncResult> SyncAsync(Guid sessionId, bool overwriteIfRemoteChanged, CancellationToken cancellationToken)
        {
            SyncCalls.Add((sessionId, overwriteIfRemoteChanged));
            return Task.FromResult(new RemoteEditSyncResult(sessionId, RemoteEditSyncOutcome.Synced));
        }

        public Task<bool> DiscardAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            DiscardCalls.Add(sessionId);
            return Task.FromResult(true);
        }
    }
    private sealed class StubRemoteReadTaskScheduler : IRemoteReadTaskScheduler
    {
        public Task RunLatestAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);

        public Task<TResult> RunLatestAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);

        public void CancelCurrent()
        {
        }
    }

    private sealed class CancelAwareRemoteReadTaskScheduler : IRemoteReadTaskScheduler
    {
        private readonly Lock _lock = new();
        private CancellationTokenSource? _currentSource;

        public async Task RunLatestAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            await RunLatestAsync(
                async token =>
                {
                    await operation(token);
                    return true;
                },
                cancellationToken);
        }

        public async Task<TResult> RunLatestAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            CancellationTokenSource current;
            lock (_lock)
            {
                _currentSource?.Cancel();
                _currentSource?.Dispose();
                current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _currentSource = current;
            }

            try
            {
                return await operation(current.Token);
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_currentSource, current))
                    {
                        _currentSource = null;
                    }
                }

                current.Dispose();
            }
        }

        public void CancelCurrent()
        {
            lock (_lock)
            {
                _currentSource?.Cancel();
            }
        }
    }

    private static async IAsyncEnumerable<RemoteSearchProgress> Stream(
        IEnumerable<RemoteSearchProgress> updates,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    private sealed class StubLocalFileOperationsService : ILocalFileOperationsService
    {
        public string? LastCreateParentPath { get; private set; }
        public string? LastCreateName { get; private set; }

        public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenWithAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken)
        {
            LastCreateParentPath = parentPath;
            LastCreateName = name;
            return Task.CompletedTask;
        }
        public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubRemoteFileOperationsService : IRemoteFileOperationsService
    {
        public SharePath? LastCreatePath { get; private set; }

        public Task CreateDirectoryAsync(SharePath path, CancellationToken cancellationToken)
        {
            LastCreatePath = path;
            return Task.CompletedTask;
        }
        public Task RenameAsync(SharePath path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(SharePath path, bool recursive, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLocalFileOperationsService : ILocalFileOperationsService
    {
        public List<string> DeletedPaths { get; } = [];

        public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenWithAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameAsync(string path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        {
            DeletedPaths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRemoteFileOperationsService : IRemoteFileOperationsService
    {
        private readonly HashSet<string> _failingRelativePaths;

        public RecordingRemoteFileOperationsService(IEnumerable<string>? failingRelativePaths = null)
        {
            _failingRelativePaths = new HashSet<string>(
                failingRelativePaths ?? [],
                StringComparer.OrdinalIgnoreCase);
        }

        public List<SharePath> DeleteAttempts { get; } = [];

        public Task CreateDirectoryAsync(SharePath path, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameAsync(SharePath path, string newName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(SharePath path, bool recursive, CancellationToken cancellationToken)
        {
            DeleteAttempts.Add(path);
            if (_failingRelativePaths.Contains(path.NormalizeRelativePath()))
            {
                throw new InvalidOperationException("Simulated delete failure.");
            }

            return Task.CompletedTask;
        }
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
        private ConnectionProfile _profile;

        public InMemoryConnectionProfileStore(ConnectionProfile? initialProfile = null)
        {
            _profile = initialProfile ?? ConnectionProfile.Empty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        public Task<ConnectionProfile> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_profile);

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            _profile = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class ReadOnlyConnectionProfileStore : IConnectionProfileStore
    {
        private readonly ConnectionProfile _profile;

        public ReadOnlyConnectionProfileStore(ConnectionProfile profile)
        {
            _profile = profile;
        }

        public Task<ConnectionProfile> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_profile);

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
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

    private sealed class PathAwareRemoteCapabilityService : IRemoteCapabilityService
    {
        public Task<RemoteCapabilitySnapshot> EvaluateAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(BuildSnapshot(context));

        public Task<RemoteCapabilitySnapshot> RefreshAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(BuildSnapshot(context));

        public RemoteCapabilitySnapshot? GetLastKnown(RemoteContext context) => BuildSnapshot(context);

        private static RemoteCapabilitySnapshot BuildSnapshot(RemoteContext context)
        {
            if (string.Equals(context.Path, "missing", StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteCapabilitySnapshot(
                    RemoteAccessState.NotFound,
                    CanBrowse: false,
                    CanUpload: false,
                    CanDownload: false,
                    CanPlanMirror: false,
                    CanExecuteMirror: false,
                    UserMessage: "The selected remote path or share was not found.",
                    EvaluatedUtc: DateTimeOffset.UtcNow,
                    ErrorCode: "ResourceNotFound",
                    HttpStatus: 404);
            }

            return RemoteCapabilitySnapshot.Accessible();
        }
    }

    private sealed class PathStateRemoteCapabilityService : IRemoteCapabilityService
    {
        private readonly string _accessiblePath;

        public PathStateRemoteCapabilityService(string accessiblePath)
        {
            _accessiblePath = accessiblePath;
        }

        public Task<RemoteCapabilitySnapshot> EvaluateAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(BuildSnapshot(context));

        public Task<RemoteCapabilitySnapshot> RefreshAsync(RemoteContext context, CancellationToken cancellationToken) =>
            Task.FromResult(BuildSnapshot(context));

        public RemoteCapabilitySnapshot? GetLastKnown(RemoteContext context) => BuildSnapshot(context);

        private RemoteCapabilitySnapshot BuildSnapshot(RemoteContext context)
        {
            var normalized = context.Path.Replace('\\', '/').Trim('/');
            if (string.Equals(normalized, _accessiblePath, StringComparison.OrdinalIgnoreCase))
            {
                return RemoteCapabilitySnapshot.Accessible();
            }

            return new RemoteCapabilitySnapshot(
                RemoteAccessState.NotFound,
                CanBrowse: false,
                CanUpload: false,
                CanDownload: false,
                CanPlanMirror: false,
                CanExecuteMirror: false,
                UserMessage: "The selected remote path or share was not found.",
                EvaluatedUtc: DateTimeOffset.UtcNow,
                ErrorCode: "ResourceNotFound",
                HttpStatus: 404);
        }
    }

    private sealed class StubRemoteActionPolicyService : IRemoteActionPolicyService
    {
        private static readonly RemoteActionPolicyService Inner = new();

        public RemoteActionPolicy Compute(RemoteCapabilitySnapshot? capability, RemoteActionInputs inputs) =>
            Inner.Compute(capability, inputs);
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






