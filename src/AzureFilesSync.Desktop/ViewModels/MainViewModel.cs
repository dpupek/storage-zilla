using System.Collections.ObjectModel;
using Azure;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;
using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.Models;
using AzureFilesSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace AzureFilesSync.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int DefaultTransferConcurrency = 4;
    private const int MinTransferConcurrency = 1;
    private const int MaxTransferConcurrency = 32;
    private const int MinTransferMaxBytesPerSecond = 0;
    private const int MaxTransferMaxBytesPerSecond = 1024 * 1024 * 1024;
    private const int RemotePageSize = 500;
    private const string QueueFilterAll = "All";
    private const string RemoteSearchScopeCurrentPath = "Current Path";
    private const string RemoteSearchScopeShareRoot = "Remote Root";
    private const int RemoteSearchMaxResults = 1000;
    private static readonly TimeSpan DefaultRemoteOpenDirectoryTimeout = TimeSpan.FromSeconds(20);

    private readonly IAuthenticationService _authenticationService;
    private readonly IAzureDiscoveryService _azureDiscoveryService;
    private readonly IStorageEndpointPreflightService _storageEndpointPreflightService;
    private readonly IRemoteOperationCoordinator _remoteOperationCoordinator;
    private readonly IPathDisplayFormatter _pathDisplayFormatter;
    private readonly ILocalBrowserService _localBrowserService;
    private readonly IAzureFilesBrowserService _azureFilesBrowserService;
    private readonly IRemoteSearchService _remoteSearchService;
    private readonly ILocalFileOperationsService _localFileOperationsService;
    private readonly IRemoteFileOperationsService _remoteFileOperationsService;
    private readonly IRemoteEditSessionService _remoteEditSessionService;
    private readonly ITransferConflictProbeService _transferConflictProbeService;
    private readonly IConflictResolutionPromptService _conflictResolutionPromptService;
    private readonly ITransferQueueService _transferQueueService;
    private readonly IMirrorPlannerService _mirrorPlanner;
    private readonly IMirrorExecutionService _mirrorExecution;
    private readonly IConnectionProfileStore _connectionProfileStore;
    private readonly IRemoteCapabilityService _remoteCapabilityService;
    private readonly IRemoteActionPolicyService _remoteActionPolicyService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IUserHelpContentService _userHelpContentService;
    private readonly TimeSpan _remoteOpenDirectoryTimeout;

    private MirrorPlan? _lastMirrorPlan;
    private bool _isRestoringProfile;
    private bool _suppressSelectionHandlers;
    private bool _isUpdatingRemoteSelection;
    private DateTimeOffset _lastLocalRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRemoteRefreshUtc = DateTimeOffset.MinValue;
    private readonly string _localPathFallbackRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _lastSuccessfulLocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _lastSuccessfulRemotePath = string.Empty;
    private readonly Lock _remoteEnrichmentLock = new();
    private readonly HashSet<string> _remoteEnrichmentInFlight = [];
    private readonly List<RemoteEntry> _selectedRemoteEntries = [];
    private string? _remoteContinuationToken;
    private long _remoteReadUiStateVersion;
    private long _remoteSearchRunVersion;
    private int _remoteSearchLastScannedEntries;
    private int _remoteSearchLastScannedDirectories;

    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];
    public ObservableCollection<StorageAccountItem> StorageAccounts { get; } = [];
    public ObservableCollection<FileShareItem> FileShares { get; } = [];
    public ObservableCollection<LocalEntry> LocalEntries { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteEntries { get; } = [];
    public ObservableCollection<LocalEntry> LocalGridEntries { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteGridEntries { get; } = [];
    public ObservableCollection<QueueItemView> QueueItems { get; } = [];
    public ObservableCollection<Guid> SelectedQueueJobIds { get; } = [];
    public ObservableCollection<string> RecentLocalPaths { get; } = [];
    public ObservableCollection<string> RecentRemotePaths { get; } = [];
    public ObservableCollection<string> QueueStatusFilterOptions { get; } =
    [
        QueueFilterAll,
        nameof(TransferJobStatus.Queued),
        nameof(TransferJobStatus.Running),
        nameof(TransferJobStatus.Paused),
        nameof(TransferJobStatus.Completed),
        nameof(TransferJobStatus.Failed),
        nameof(TransferJobStatus.Canceled)
    ];
    public ObservableCollection<string> QueueDirectionFilterOptions { get; } =
    [
        QueueFilterAll,
        nameof(TransferDirection.Upload),
        nameof(TransferDirection.Download)
    ];
    public ObservableCollection<string> RemoteSearchScopeOptions { get; } =
    [
        RemoteSearchScopeCurrentPath,
        RemoteSearchScopeShareRoot
    ];
    public IReadOnlyList<string> RecentRemotePathDisplayOptions
    {
        get
        {
            var results = new List<string> { FormatRemotePathDisplay(string.Empty) };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };

            foreach (var path in RecentRemotePaths)
            {
                var normalized = NormalizeRemotePathDisplay(path);
                if (!seen.Add(normalized))
                {
                    continue;
                }

                results.Add(FormatRemotePathDisplay(normalized));
            }

            return results;
        }
    }
    public ICollectionView QueueItemsView { get; }

    [ObservableProperty]
    private string _loginStatus = "Not signed in";

    [ObservableProperty]
    private string _queueBatchStatusMessage = string.Empty;

    [ObservableProperty]
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    private string _remotePath = string.Empty;

    [ObservableProperty]
    private string _remotePaneStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRemoteDiagnosticsCommand))]
    private string _remoteDiagnosticsDetails = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreRemoteEntriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRemoteSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRemoteSearchCommand))]
    private bool _isRemoteLoading;

    [ObservableProperty]
    private bool _isRemoteSpinnerVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreRemoteEntriesCommand))]
    private bool _hasMoreRemoteEntries;

    [ObservableProperty]
    private string _remoteLoadingMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRemoteSearchCommand))]
    private string _remoteSearchQuery = string.Empty;

    [ObservableProperty]
    private string _remoteSearchStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearRemoteSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreRemoteEntriesCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRemoteSearchCommand))]
    private bool _isRemoteSearchActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchRemoteCommand))]
    private string _selectedRemoteSearchScope = RemoteSearchScopeCurrentPath;

    [ObservableProperty]
    private bool _includeDeletes;

    [ObservableProperty]
    private int _transferMaxConcurrency = DefaultTransferConcurrency;

    [ObservableProperty]
    private int _transferMaxBytesPerSecond;

    [ObservableProperty]
    private TransferConflictPolicy _uploadConflictDefaultPolicy = TransferConflictPolicy.Ask;

    [ObservableProperty]
    private TransferConflictPolicy _downloadConflictDefaultPolicy = TransferConflictPolicy.Ask;

    [ObservableProperty]
    private UpdateChannel _updateChannel = UpdateChannel.Stable;

    public int TransferMaxKilobytesPerSecond
    {
        get => TransferMaxBytesPerSecond <= 0 ? 0 : Math.Max(1, TransferMaxBytesPerSecond / 1024);
        set
        {
            var normalizedKb = Math.Max(0, value);
            var bytesPerSecond = normalizedKb <= 0
                ? 0
                : NormalizeTransferMaxBytesPerSecond((int)Math.Min((long)normalizedKb * 1024, int.MaxValue));
            TransferMaxBytesPerSecond = bytesPerSecond;
        }
    }

    public string StatusThrottleText =>
        TransferMaxKilobytesPerSecond <= 0
            ? "Throttle: Unlimited"
            : $"Throttle: {TransferMaxKilobytesPerSecond:N0} KB/s";

    public string StatusConcurrencyText => $"Concurrency: {TransferMaxConcurrency}";
    public string StatusQueueText => string.IsNullOrWhiteSpace(QueueBatchStatusMessage) ? "Queue: idle" : QueueBatchStatusMessage;
    public bool IsRemoteGridEnabled => !IsRemoteLoading;
    public bool IsRemoteLoadingOverlayVisible => IsRemoteLoading && !IsRemoteSearchActive;

    public string RemotePathDisplay
    {
        get => FormatRemotePathDisplay(RemotePath);
        set
        {
            var normalized = NormalizeRemotePathDisplay(value);
            if (!string.Equals(RemotePath, normalized, StringComparison.Ordinal))
            {
                RemotePath = normalized;
            }
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    private bool _isMirrorPlanning;

    [ObservableProperty]
    private string _mirrorPlanStatusMessage = string.Empty;

    [ObservableProperty]
    private string _updateStatusMessage = "Updates: idle";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchRemoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreRemoteEntriesCommand))]
    private RemoteCapabilitySnapshot? _remoteCapability;

    [ObservableProperty]
    private SubscriptionItem? _selectedSubscription;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchRemoteCommand))]
    private StorageAccountItem? _selectedStorageAccount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchRemoteCommand))]
    private FileShareItem? _selectedFileShare;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    private LocalEntry? _selectedLocalEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    private RemoteEntry? _selectedRemoteEntry;

    [ObservableProperty]
    private QueueItemView? _selectedQueueItem;

    [ObservableProperty]
    private int _selectedQueueCount;

    [ObservableProperty]
    private string _selectedQueueStatusFilter = QueueFilterAll;

    [ObservableProperty]
    private string _selectedQueueDirectionFilter = QueueFilterAll;

    [ObservableProperty]
    private GridLayoutProfile? _localGridLayout;

    [ObservableProperty]
    private GridLayoutProfile? _remoteGridLayout;

    public MainViewModel(
        IAuthenticationService authenticationService,
        IAzureDiscoveryService azureDiscoveryService,
        IStorageEndpointPreflightService storageEndpointPreflightService,
        IRemoteReadTaskScheduler remoteReadTaskScheduler,
        ILocalBrowserService localBrowserService,
        IAzureFilesBrowserService azureFilesBrowserService,
        IRemoteSearchService remoteSearchService,
        ILocalFileOperationsService localFileOperationsService,
        IRemoteFileOperationsService remoteFileOperationsService,
        ITransferConflictProbeService transferConflictProbeService,
        IConflictResolutionPromptService conflictResolutionPromptService,
        ITransferQueueService transferQueueService,
        IMirrorPlannerService mirrorPlanner,
        IMirrorExecutionService mirrorExecution,
        IConnectionProfileStore connectionProfileStore,
        IRemoteCapabilityService remoteCapabilityService,
        IRemoteActionPolicyService remoteActionPolicyService,
        IAppUpdateService appUpdateService,
        IUserHelpContentService userHelpContentService,
        TimeSpan? remoteOpenDirectoryTimeout = null,
        IRemoteOperationCoordinator? remoteOperationCoordinator = null,
        IPathDisplayFormatter? pathDisplayFormatter = null,
        IRemoteEditSessionService? remoteEditSessionService = null)
    {
        _authenticationService = authenticationService;
        _azureDiscoveryService = azureDiscoveryService;
        _storageEndpointPreflightService = storageEndpointPreflightService;
        _remoteOperationCoordinator = remoteOperationCoordinator ?? new SchedulerBackedRemoteOperationCoordinator(remoteReadTaskScheduler);
        _pathDisplayFormatter = pathDisplayFormatter ?? new PathDisplayFormatter();
        _localBrowserService = localBrowserService;
        _azureFilesBrowserService = azureFilesBrowserService;
        _remoteSearchService = remoteSearchService;
        _localFileOperationsService = localFileOperationsService;
        _remoteFileOperationsService = remoteFileOperationsService;
        _transferConflictProbeService = transferConflictProbeService;
        _conflictResolutionPromptService = conflictResolutionPromptService;
        _transferQueueService = transferQueueService;
        _mirrorPlanner = mirrorPlanner;
        _mirrorExecution = mirrorExecution;
        _connectionProfileStore = connectionProfileStore;
        _remoteCapabilityService = remoteCapabilityService;
        _remoteActionPolicyService = remoteActionPolicyService;
        _appUpdateService = appUpdateService;
        _userHelpContentService = userHelpContentService;
        _remoteEditSessionService = remoteEditSessionService ?? NoOpRemoteEditSessionService.Instance;
        _remoteOpenDirectoryTimeout = remoteOpenDirectoryTimeout ?? DefaultRemoteOpenDirectoryTimeout;
        UpdateChannel = _appUpdateService.CurrentChannel;
        RecentRemotePaths.CollectionChanged += OnRecentRemotePathsCollectionChanged;

        QueueItemsView = CollectionViewSource.GetDefaultView(QueueItems);
        QueueItemsView.Filter = ShouldIncludeQueueItem;
        _transferQueueService.JobUpdated += OnJobUpdated;
        _ = LoadLocalProfileDefaultsAsync();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        try
        {
            Log.Information("User initiated interactive sign-in.");
            var session = await _authenticationService.SignInInteractiveAsync(CancellationToken.None);
            LoginStatus = session.UsedFallback
                ? $"Signed in as {session.DisplayName} (browser fallback)."
                : $"Signed in as {session.DisplayName}";
            Log.Information(
                "Sign-in completed for {DisplayName}. AuthMode={AuthMode} UsedFallback={UsedFallback}",
                session.DisplayName,
                session.AuthMode,
                session.UsedFallback);

            var subscriptions = new List<SubscriptionItem>();
            await foreach (var subscription in _azureDiscoveryService.ListSubscriptionsAsync(CancellationToken.None))
            {
                subscriptions.Add(subscription);
            }

            ReplaceSortedCollection(Subscriptions, subscriptions, x => x.Name);
            Log.Debug("Loaded {SubscriptionCount} subscriptions.", Subscriptions.Count);

            await ApplyProfileSelectionsAsync();
            await LoadLocalDirectoryAsync();
        }
        catch (Exception ex)
        {
            ShowError("Sign in failed.", ex);
        }
    }

    [RelayCommand]
    private async Task LoadLocalDirectoryAsync()
    {
        var previousPath = _lastSuccessfulLocalPath;
        var requestedPath = NormalizeLocalPath(LocalPath);

        try
        {
            Log.Debug("Loading local directory: {LocalPath}", requestedPath);
            var loadedEntries = await _localBrowserService.ListDirectoryAsync(requestedPath, CancellationToken.None);

            LocalEntries.Clear();
            foreach (var item in loadedEntries)
            {
                LocalEntries.Add(item);
            }

            LocalPath = requestedPath;
            _lastSuccessfulLocalPath = requestedPath;
            RefreshLocalGridEntries();
            Log.Debug("Loaded {EntryCount} local entries from {LocalPath}", LocalEntries.Count, requestedPath);

            AddRecentPath(RecentLocalPaths, requestedPath);
            await PersistProfileAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            LocalPath = previousPath;
            Log.Warning(ex, "Access denied while loading local directory {LocalPath}", requestedPath);
            MessageBox.Show(
                $"Access denied for '{requestedPath}'. Choose a folder you can read, or run with elevated permissions.",
                "Local Folder Access Denied",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LocalPath = previousPath;
            ShowError("Failed to load local directory.", ex);
        }
    }

    [RelayCommand]
    private async Task LoadRemoteDirectoryAsync()
    {
        ClearRemoteSearchState(clearQuery: false);
        var previousPath = _lastSuccessfulRemotePath;
        var snapshot = CaptureRemoteViewSnapshot();
        await ExecuteRemoteReadTaskAsync(
            operationType: RemoteOperationType.Browse,
            loadingMessage: "Loading remote directory...",
            clearGridFirst: false,
            showSpinnerAfterDelay: false,
            errorMessage: "Failed to load remote directory.",
            operation: token => LoadRemoteDirectoryPageAsync(reset: true, token),
            onFailureRollbackAsync: () =>
            {
                RestoreRemoteViewSnapshot(snapshot);
                RemotePath = previousPath;
                return Task.CompletedTask;
            });
    }

    public async Task NavigateRemotePathFromInputAsync(string? requestedPathDisplay)
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        ClearRemoteSearchState(clearQuery: false);
        var requestedPath = NormalizeRemotePathDisplay(requestedPathDisplay);
        var previousPath = RemotePath;
        var candidatePaths = BuildRemotePathFallbackCandidates(requestedPath, previousPath);
        string? successfulPath = null;
        RemoteAccessState? requestedPathFailureState = null;
        const int maxPathAttempts = 3;

        var success = await ExecuteRemoteReadTaskAsync(
            operationType: RemoteOperationType.Browse,
            loadingMessage: "Loading remote directory...",
            clearGridFirst: false,
            showSpinnerAfterDelay: false,
            errorMessage: "Failed to load remote directory.",
            operation: async token =>
            {
                var attemptedPaths = new HashSet<string>(StringComparer.Ordinal);
                var attemptCount = 0;

                foreach (var candidatePath in candidatePaths)
                {
                    if (!attemptedPaths.Add(candidatePath))
                    {
                        continue;
                    }

                    attemptCount++;
                    if (attemptCount > maxPathAttempts)
                    {
                        Log.Warning(
                            "Remote path fallback aborted after max attempts. RequestedPath={RequestedPath} PreviousPath={PreviousPath} Account={Account} Root={Root}",
                            requestedPath,
                            previousPath,
                            SelectedStorageAccount.Name,
                            SelectedFileShare.Name);
                        break;
                    }

                    token.ThrowIfCancellationRequested();
                    RemotePath = candidatePath;

                    Log.Debug(
                        "Attempting remote path load candidate {Attempt}/{MaxAttempts}. CandidatePath={CandidatePath} RequestedPath={RequestedPath} PreviousPath={PreviousPath} Account={Account} Root={Root}",
                        attemptCount,
                        maxPathAttempts,
                        candidatePath,
                        requestedPath,
                        previousPath,
                        SelectedStorageAccount.Name,
                        SelectedFileShare.Name);

                    var loaded = await LoadRemoteDirectoryPageAsync(
                        reset: true,
                        token,
                        showUnexpectedErrorDialog: false,
                        allowRootFallback: false);
                    if (loaded)
                    {
                        successfulPath = candidatePath;
                        return true;
                    }

                    if (string.Equals(candidatePath, requestedPath, StringComparison.Ordinal))
                    {
                        requestedPathFailureState = RemoteCapability?.State;
                    }
                }

                return false;
            },
            onFailureRollbackAsync: null);

        if (success &&
            !string.Equals(successfulPath, requestedPath, StringComparison.Ordinal) &&
            requestedPathFailureState == RemoteAccessState.NotFound)
        {
            MessageBox.Show(
                $"The path '{FormatRemotePathDisplay(requestedPath)}' was not found in remote root '{SelectedFileShare.Name}'. The view was restored to '{FormatRemotePathDisplay(successfulPath)}'.",
                "Remote Path Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else if (!success && requestedPathFailureState == RemoteAccessState.NotFound)
        {
            MessageBox.Show(
                $"The path '{FormatRemotePathDisplay(requestedPath)}' was not found in remote root '{SelectedFileShare.Name}', and no fallback path could be opened.",
                "Remote Path Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreRemoteEntries))]
    private async Task LoadMoreRemoteEntriesAsync()
    {
        await ExecuteRemoteReadTaskAsync(
            operationType: RemoteOperationType.LoadMore,
            loadingMessage: "Loading more remote entries...",
            clearGridFirst: false,
            showSpinnerAfterDelay: false,
            errorMessage: "Failed to load more remote entries.",
            operation: token => LoadRemoteDirectoryPageAsync(reset: false, token),
            onFailureRollbackAsync: null);
    }

    [RelayCommand(CanExecute = nameof(CanSearchRemote))]
    private async Task SearchRemoteAsync()
    {
        var query = RemoteSearchQuery?.Trim() ?? string.Empty;
        if (IsRemoteLoading)
        {
            Log.Debug("Remote search request ignored because remote loading is already in progress.");
            return;
        }

        if (string.IsNullOrWhiteSpace(query) || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var searchRunVersion = Interlocked.Increment(ref _remoteSearchRunVersion);
        var searchRunId = Guid.NewGuid();
        var searchStartedAtUtc = DateTimeOffset.UtcNow;
        Log.Debug(
            "Remote search requested. RunId={RunId} Version={Version} Query={Query} Account={Account} Share={Share} Scope={Scope} Path={Path}",
            searchRunId,
            searchRunVersion,
            query,
            SelectedStorageAccount.Name,
            SelectedFileShare.Name,
            SelectedRemoteSearchScope,
            RemotePath);
        var snapshot = CaptureRemoteViewSnapshot();
        var success = await ExecuteRemoteReadTaskAsync(
            operationType: RemoteOperationType.Search,
            loadingMessage: $"Searching '{query}'...",
            clearGridFirst: true,
            showSpinnerAfterDelay: true,
            errorMessage: "Failed to search the remote server.",
            operation: async token =>
            {
                if (!IsCurrentRemoteSearchRun(searchRunVersion))
                {
                    Log.Debug(
                        "Remote search stale run ignored at start. RunId={RunId} Version={Version} Query={Query}",
                        searchRunId,
                        searchRunVersion,
                        query);
                    return false;
                }

                var context = BuildRemoteContext();
                var capability = await _remoteCapabilityService.RefreshAsync(context, token);
                if (!IsCurrentRemoteSearchRun(searchRunVersion))
                {
                    Log.Debug(
                        "Remote search stale run ignored after capability refresh. RunId={RunId} Version={Version} Query={Query}",
                        searchRunId,
                        searchRunVersion,
                        query);
                    return false;
                }

                ApplyCapability(capability);
                if (!capability.CanBrowse)
                {
                    Log.Warning(
                        "Remote search aborted because remote browse capability is unavailable. RunId={RunId} Version={Version} Query={Query} Reason={Reason}",
                        searchRunId,
                        searchRunVersion,
                        query,
                        capability.UserMessage);
                    return false;
                }

                var startRelativePath = string.Equals(SelectedRemoteSearchScope, RemoteSearchScopeShareRoot, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : RemotePath;
                RemoteEntries.Clear();
                _remoteContinuationToken = null;
                HasMoreRemoteEntries = false;
                IsRemoteSearchActive = true;
                _remoteSearchLastScannedEntries = 0;
                _remoteSearchLastScannedDirectories = 0;
                RemoteSearchStatusMessage = $"Searching '{query}'... 0 item(s) scanned.";
                RefreshRemoteGridEntries();
                var scopeLabel = string.Equals(SelectedRemoteSearchScope, RemoteSearchScopeShareRoot, StringComparison.OrdinalIgnoreCase)
                    ? "share root"
                    : (string.IsNullOrWhiteSpace(RemotePath) ? "current path (root)" : $"current path '{RemotePath}'");
                SelectedRemoteEntry = null;
                _selectedRemoteEntries.Clear();
                EnqueueDownloadCommand.NotifyCanExecuteChanged();

                var final = new RemoteSearchProgress([], 0, IsCompleted: true, IsTruncated: false, 0, 0);
                var lastProgressLogEntries = -1;
                var lastProgressLogDirectories = -1;
                await foreach (var progress in _remoteSearchService.SearchIncrementalAsync(
                                   new RemoteSearchRequest(
                                       BuildSelectedSharePath(startRelativePath),
                                       query,
                                       IncludeDirectories: true,
                                       MaxResults: RemoteSearchMaxResults),
                                   token))
                {
                    if (!IsCurrentRemoteSearchRun(searchRunVersion))
                    {
                        return false;
                    }

                    final = progress;
                    _remoteSearchLastScannedEntries = progress.ScannedEntries;
                    _remoteSearchLastScannedDirectories = progress.ScannedDirectories;
                    if (progress.NewMatches.Count > 0)
                    {
                        foreach (var match in progress.NewMatches)
                        {
                            RemoteEntries.Add(match);
                        }

                        RefreshRemoteGridEntries();
                        Log.Debug(
                            "Remote search matches applied. Query={Query} NewMatches={NewMatches} TotalMatches={TotalMatches} RemoteEntries={RemoteEntriesCount} RemoteGridEntries={RemoteGridEntriesCount}",
                            query,
                            progress.NewMatches.Count,
                            progress.TotalMatches,
                            RemoteEntries.Count,
                            RemoteGridEntries.Count);
                    }
                    else if (progress.SnapshotMatches is { Count: > 0 } snapshot &&
                             snapshot.Count != RemoteEntries.Count)
                    {
                        RemoteEntries.Clear();
                        foreach (var match in snapshot)
                        {
                            RemoteEntries.Add(match);
                        }

                        RefreshRemoteGridEntries();
                        Log.Debug(
                            "Remote search snapshot reconciled. Query={Query} SnapshotMatches={SnapshotMatches} RemoteEntries={RemoteEntriesCount} RemoteGridEntries={RemoteGridEntriesCount}",
                            query,
                            snapshot.Count,
                            RemoteEntries.Count,
                            RemoteGridEntries.Count);
                    }
                    else if (progress.TotalMatches > RemoteEntries.Count)
                    {
                        Log.Warning(
                            "Remote search progress mismatch. Query={Query} ProgressTotalMatches={ProgressTotalMatches} RemoteEntries={RemoteEntriesCount} RemoteGridEntries={RemoteGridEntriesCount}",
                            query,
                            progress.TotalMatches,
                            RemoteEntries.Count,
                            RemoteGridEntries.Count);
                    }

                    if (progress.IsCompleted)
                    {
                        RemoteSearchStatusMessage = progress.IsTruncated
                            ? $"Showing first {progress.TotalMatches} match(es) for '{query}' from {scopeLabel}. Searched {progress.ScannedEntries} item(s) in {progress.ScannedDirectories} folder(s). Refine search for more."
                            : $"Found {progress.TotalMatches} match(es) for '{query}' from {scopeLabel}. Searched {progress.ScannedEntries} item(s) in {progress.ScannedDirectories} folder(s).";
                    }
                    else
                    {
                        RemoteSearchStatusMessage =
                            $"Searching '{query}'... {progress.TotalMatches} match(es), {progress.ScannedEntries} item(s) scanned in {progress.ScannedDirectories} folder(s).";
                    }

                    var shouldLogProgress =
                        progress.IsCompleted ||
                        progress.ScannedEntries >= lastProgressLogEntries + 5000 ||
                        progress.ScannedDirectories >= lastProgressLogDirectories + 250 ||
                        (progress.ScannedDirectories > 0 && lastProgressLogDirectories < 0);
                    if (shouldLogProgress)
                    {
                        Log.Debug(
                            "Remote search progress. Query={Query} Matches={Matches} ScannedDirectories={ScannedDirectories} ScannedEntries={ScannedEntries} Completed={Completed}",
                            query,
                            progress.TotalMatches,
                            progress.ScannedDirectories,
                            progress.ScannedEntries,
                            progress.IsCompleted);
                        lastProgressLogEntries = progress.ScannedEntries;
                        lastProgressLogDirectories = progress.ScannedDirectories;
                    }
                }

                if (!IsCurrentRemoteSearchRun(searchRunVersion))
                {
                    Log.Debug(
                        "Remote search stale run ignored before completion. RunId={RunId} Version={Version} Query={Query}",
                        searchRunId,
                        searchRunVersion,
                        query);
                    return false;
                }

                Log.Debug(
                    "Remote search completed. RunId={RunId} Version={Version} Query={Query} Matches={Matches} Truncated={Truncated} Account={Account} Share={Share} StartPath={StartPath} ScannedDirectories={ScannedDirectories} ScannedEntries={ScannedEntries}",
                    searchRunId,
                    searchRunVersion,
                    query,
                    final.TotalMatches,
                    final.IsTruncated,
                    SelectedStorageAccount.Name,
                    SelectedFileShare.Name,
                    startRelativePath,
                    final.ScannedDirectories,
                    final.ScannedEntries);
                return true;
            },
            onFailureRollbackAsync: () =>
            {
                if (!IsCurrentRemoteSearchRun(searchRunVersion))
                {
                    return Task.CompletedTask;
                }

                RestoreRemoteViewSnapshot(snapshot);
                ClearRemoteSearchState(clearQuery: false);
                return Task.CompletedTask;
            });

        Log.Debug(
            "Remote search finished. RunId={RunId} Version={Version} Success={Success} Query={Query} DurationMs={DurationMs} LastScannedEntries={ScannedEntries} LastScannedDirectories={ScannedDirectories} IsRemoteLoading={IsRemoteLoading} IsRemoteSearchActive={IsRemoteSearchActive}",
            searchRunId,
            searchRunVersion,
            success,
            query,
            (DateTimeOffset.UtcNow - searchStartedAtUtc).TotalMilliseconds,
            _remoteSearchLastScannedEntries,
            _remoteSearchLastScannedDirectories,
            IsRemoteLoading,
            IsRemoteSearchActive);
    }

    [RelayCommand(CanExecute = nameof(CanClearRemoteSearch))]
    private async Task ClearRemoteSearchAsync()
    {
        if (!IsRemoteSearchActive)
        {
            return;
        }

        ClearRemoteSearchState(clearQuery: false);
        await LoadRemoteDirectoryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCancelRemoteSearch))]
    private Task CancelRemoteSearchAsync()
    {
        if (!IsRemoteLoading || !IsRemoteSearchActive)
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _remoteSearchRunVersion);
        _remoteOperationCoordinator.CancelCurrent(RemoteOperationCancelReason.UserRequested);
        RemoteSearchStatusMessage =
            $"Search canceled. Showing {RemoteEntries.Count} match(es). Searched {_remoteSearchLastScannedEntries} item(s) in {_remoteSearchLastScannedDirectories} folder(s).";
        Log.Debug("Remote search cancel requested.");
        return Task.CompletedTask;
    }

    private async Task<bool> ExecuteRemoteReadTaskAsync(
        RemoteOperationType operationType,
        string loadingMessage,
        bool clearGridFirst,
        bool showSpinnerAfterDelay,
        string errorMessage,
        Func<CancellationToken, Task<bool>> operation,
        Func<Task>? onFailureRollbackAsync,
        TimeSpan? operationTimeout = null,
        string? timeoutMessage = null)
    {
        var invocationVersion = Interlocked.Increment(ref _remoteReadUiStateVersion);

        if (clearGridFirst)
        {
            ClearRemoteEntriesAndPaging();
        }

        IsRemoteLoading = true;
        RemoteLoadingMessage = loadingMessage;
        IsRemoteSpinnerVisible = false;

        using var spinnerCts = new CancellationTokenSource();
        using var operationTimeoutCts = operationTimeout.HasValue
            ? new CancellationTokenSource(operationTimeout.Value)
            : null;
        var schedulerCancellationToken = operationTimeoutCts?.Token ?? CancellationToken.None;
        var spinnerTask = showSpinnerAfterDelay
            ? DelayShowRemoteSpinnerAsync(spinnerCts.Token)
            : Task.CompletedTask;
        RemoteOperationScope? operationScope = null;

        try
        {
            var success = await _remoteOperationCoordinator.RunLatestAsync(
                operationType,
                (scope, token) =>
                {
                    operationScope = scope;
                    Log.Debug(
                        "Remote operation started. Type={OperationType} CorrelationId={CorrelationId} Sequence={Sequence} Message={LoadingMessage}",
                        scope.OperationType,
                        scope.CorrelationId,
                        scope.Sequence,
                        loadingMessage);
                    return RunRemoteOperationOnUiThreadAsync(operation, token);
                },
                schedulerCancellationToken);
            if (operationScope.HasValue)
            {
                var scope = operationScope.Value;
                Log.Debug(
                    "Remote operation completed. Type={OperationType} CorrelationId={CorrelationId} Sequence={Sequence} Success={Success} Message={LoadingMessage}",
                    scope.OperationType,
                    scope.CorrelationId,
                    scope.Sequence,
                    success,
                    loadingMessage);
            }

            if (!IsCurrentRemoteReadInvocation(invocationVersion))
            {
                Log.Debug("Ignored stale remote read completion. Message={LoadingMessage}", loadingMessage);
                return false;
            }

            if (!success && onFailureRollbackAsync is not null)
            {
                await onFailureRollbackAsync();
            }

            return success;
        }
        catch (OperationCanceledException) when (operationTimeoutCts?.IsCancellationRequested == true)
        {
            if (!IsCurrentRemoteReadInvocation(invocationVersion))
            {
                Log.Debug("Ignored stale remote read timeout. Message={LoadingMessage}", loadingMessage);
                return false;
            }

            if (onFailureRollbackAsync is not null)
            {
                await onFailureRollbackAsync();
            }

            if (!string.IsNullOrWhiteSpace(timeoutMessage))
            {
                QueueBatchStatusMessage = timeoutMessage;
                OnPropertyChanged(nameof(StatusQueueText));
            }

            Log.Warning(
                "Remote read operation timed out. Message={LoadingMessage} OperationType={OperationType} CorrelationId={CorrelationId}",
                loadingMessage,
                operationType,
                operationScope?.CorrelationId);
            return false;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRemoteReadInvocation(invocationVersion))
            {
                Log.Debug(
                    "Remote read operation canceled or replaced. Message={LoadingMessage} CancelReason={CancelReason} OperationType={OperationType} CorrelationId={CorrelationId}",
                    loadingMessage,
                    _remoteOperationCoordinator.LastCancelReason,
                    operationType,
                    operationScope?.CorrelationId);
            }
            else
            {
                Log.Debug("Ignored stale remote read cancellation. Message={LoadingMessage}", loadingMessage);
            }

            return false;
        }
        catch (Exception ex)
        {
            if (!IsCurrentRemoteReadInvocation(invocationVersion))
            {
                Log.Debug(ex, "Ignored stale remote read failure. Message={LoadingMessage}", loadingMessage);
                return false;
            }

            if (onFailureRollbackAsync is not null)
            {
                await onFailureRollbackAsync();
            }

            Log.Error(
                ex,
                "Remote read operation failed. Message={LoadingMessage} OperationType={OperationType} CorrelationId={CorrelationId}",
                loadingMessage,
                operationType,
                operationScope?.CorrelationId);
            ShowError(errorMessage, ex);
            return false;
        }
        finally
        {
            spinnerCts.Cancel();
            try
            {
                await spinnerTask;
            }
            catch (OperationCanceledException)
            {
                // Ignore delayed spinner cancellation.
            }

            if (IsCurrentRemoteReadInvocation(invocationVersion))
            {
                IsRemoteSpinnerVisible = false;
                IsRemoteLoading = false;
                RemoteLoadingMessage = string.Empty;
                NotifyCanExecuteChangedSafe(() => LoadMoreRemoteEntriesCommand.NotifyCanExecuteChanged());
            }
        }
    }

    private static Task<bool> RunRemoteOperationOnUiThreadAsync(
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return operation(cancellationToken);
        }

        return dispatcher
            .InvokeAsync(() => operation(cancellationToken), DispatcherPriority.Normal, cancellationToken)
            .Task
            .Unwrap();
    }

    private async Task DelayShowRemoteSpinnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            IsRemoteSpinnerVisible = true;
        }
        catch (OperationCanceledException)
        {
            // No-op; operation completed before spinner threshold.
        }
    }

    private bool IsCurrentRemoteReadInvocation(long invocationVersion) =>
        Interlocked.Read(ref _remoteReadUiStateVersion) == invocationVersion;

    private bool IsCurrentRemoteSearchRun(long searchRunVersion) =>
        Interlocked.Read(ref _remoteSearchRunVersion) == searchRunVersion;

    private static RemoteCapabilitySnapshot BuildRemoteNotFoundSnapshot() =>
        new(
            RemoteAccessState.NotFound,
            false,
            false,
            false,
            false,
            false,
            "The selected remote path or root was not found.",
            DateTimeOffset.UtcNow,
            ErrorCode: "ResourceNotFound",
            HttpStatus: 404);

    private SharePath BuildSelectedSharePath(string relativePath)
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            throw new InvalidOperationException("A storage account and remote root must be selected.");
        }

        return new SharePath(
            SelectedStorageAccount.Name,
            SelectedFileShare.Name,
            relativePath,
            SelectedFileShare.ProviderKind);
    }

    private async Task<bool> LoadRemoteDirectoryPageAsync(
        bool reset,
        CancellationToken cancellationToken,
        bool showUnexpectedErrorDialog = true,
        bool allowRootFallback = true)
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            ClearRemoteEntriesAndPaging();
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Select a storage account and remote root."));
            return false;
        }

        if (!reset && string.IsNullOrWhiteSpace(_remoteContinuationToken))
        {
            HasMoreRemoteEntries = false;
            return false;
        }

        var context = BuildRemoteContext();

        try
        {
            var capability = await _remoteCapabilityService.RefreshAsync(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCapability(capability);

            if (!capability.CanBrowse)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryFallbackToRemoteRootOnNotFoundAsync(
                        context,
                        capability,
                        reset,
                        showUnexpectedErrorDialog,
                        allowRootFallback,
                        cancellationToken))
                {
                    return true;
                }

                if (reset)
                {
                    ClearRemoteEntriesAndPaging();
                }

                return false;
            }

            var path = new SharePath(context.StorageAccountName, context.ShareName, context.Path, context.ProviderKind);
            var continuationToken = reset ? null : _remoteContinuationToken;
            var page = await _azureFilesBrowserService.ListDirectoryPageAsync(path, continuationToken, RemotePageSize, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (reset &&
                context.ProviderKind == RemoteProviderKind.AzureBlob &&
                string.IsNullOrWhiteSpace(continuationToken) &&
                !string.IsNullOrWhiteSpace(context.Path) &&
                page.Entries.Count == 0)
            {
                // Blob virtual folders can return an empty page for missing prefixes; verify directory existence explicitly.
                var details = await _azureFilesBrowserService.GetEntryDetailsAsync(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (details is null || !details.IsDirectory)
                {
                    var notFound = BuildRemoteNotFoundSnapshot();
                    ApplyCapability(notFound);
                    if (await TryFallbackToRemoteRootOnNotFoundAsync(
                            context,
                            notFound,
                            reset,
                            showUnexpectedErrorDialog,
                            allowRootFallback,
                            cancellationToken))
                    {
                        return true;
                    }

                    ClearRemoteEntriesAndPaging();
                    Log.Warning(
                        "Remote blob path resolved to no directory. Treating as not found. Account={Account} Root={Root} Path={Path}",
                        context.StorageAccountName,
                        context.ShareName,
                        context.Path);
                    return false;
                }
            }

            if (reset)
            {
                RemoteEntries.Clear();
            }

            foreach (var item in page.Entries)
            {
                RemoteEntries.Add(item);
            }

            _remoteContinuationToken = page.ContinuationToken;
            HasMoreRemoteEntries = page.HasMore;
            RefreshRemoteGridEntries();

            if (reset)
            {
                AddRecentPath(RecentRemotePaths, RemotePath);
                OnPropertyChanged(nameof(RemotePathDisplay));
                await PersistProfileAsync();
            }
            _lastSuccessfulRemotePath = context.Path;

            Log.Debug(
                "Loaded remote entries page. Reset={Reset} Added={AddedCount} Total={TotalCount} HasMore={HasMore} Account={Account} Share={Share} Path={RemotePath}",
                reset,
                page.Entries.Count,
                RemoteEntries.Count,
                page.HasMore,
                context.StorageAccountName,
                context.ShareName,
                context.Path);

            return true;
        }
        catch (RequestFailedException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capability = await _remoteCapabilityService.RefreshAsync(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCapability(capability);

            if (await TryFallbackToRemoteRootOnNotFoundAsync(
                    context,
                    capability,
                    reset,
                    showUnexpectedErrorDialog,
                    allowRootFallback,
                    cancellationToken))
            {
                return true;
            }

            if (reset)
            {
                ClearRemoteEntriesAndPaging();
            }

            if (capability.State is RemoteAccessState.PermissionDenied or RemoteAccessState.NotFound or RemoteAccessState.TransientFailure)
            {
                Log.Warning(ex, "Remote directory load mapped to capability state {State}", capability.State);
                return false;
            }

            if (showUnexpectedErrorDialog)
            {
                ShowError("Failed to load remote directory.", ex);
            }
            else
            {
                Log.Warning(ex, "Remote directory load failed without modal error dialog (reset={Reset}).", reset);
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reset)
            {
                ClearRemoteEntriesAndPaging();
            }

            if (showUnexpectedErrorDialog)
            {
                ShowError("Failed to load remote directory.", ex);
            }
            else
            {
                Log.Warning(ex, "Remote directory load failed without modal error dialog (reset={Reset}).", reset);
            }
            return false;
        }
    }

    private async Task<bool> TryFallbackToRemoteRootOnNotFoundAsync(
        RemoteContext context,
        RemoteCapabilitySnapshot capability,
        bool reset,
        bool showUnexpectedErrorDialog,
        bool allowRootFallback,
        CancellationToken cancellationToken)
    {
        if (!reset ||
            !allowRootFallback ||
            capability.State != RemoteAccessState.NotFound ||
            string.IsNullOrWhiteSpace(context.Path))
        {
            return false;
        }

        Log.Warning(
            "Remote path was not found. Retrying at share root. Account={Account} Share={Share} Path={Path}",
            context.StorageAccountName,
            context.ShareName,
            context.Path);

        cancellationToken.ThrowIfCancellationRequested();
        RemotePath = string.Empty;
        return await LoadRemoteDirectoryPageAsync(
            reset: true,
            cancellationToken,
            showUnexpectedErrorDialog,
            allowRootFallback: false);
    }

    private void ClearRemoteEntriesAndPaging()
    {
        _remoteContinuationToken = null;
        HasMoreRemoteEntries = false;
        RemoteEntries.Clear();
        RemoteGridEntries.Clear();
        SelectedRemoteEntry = null;
        _selectedRemoteEntries.Clear();
    }

    private void ClearRemoteSearchState(bool clearQuery)
    {
        IsRemoteSearchActive = false;
        RemoteSearchStatusMessage = string.Empty;
        _remoteSearchLastScannedEntries = 0;
        _remoteSearchLastScannedDirectories = 0;
        if (clearQuery)
        {
            RemoteSearchQuery = string.Empty;
        }
    }

    private RemoteViewSnapshot CaptureRemoteViewSnapshot()
    {
        var selectedEntries = new List<RemoteEntry>();
        if (_selectedRemoteEntries.Count > 0)
        {
            selectedEntries.AddRange(_selectedRemoteEntries);
        }
        else if (SelectedRemoteEntry is not null)
        {
            selectedEntries.Add(SelectedRemoteEntry);
        }

        return new RemoteViewSnapshot(
            RemotePath,
            [.. RemoteEntries],
            SelectedRemoteEntry,
            selectedEntries,
            _remoteContinuationToken,
            HasMoreRemoteEntries);
    }

    private void RestoreRemoteViewSnapshot(RemoteViewSnapshot snapshot)
    {
        _lastSuccessfulRemotePath = snapshot.Path;
        RemoteEntries.Clear();
        foreach (var item in snapshot.Entries)
        {
            RemoteEntries.Add(item);
        }

        _remoteContinuationToken = snapshot.ContinuationToken;
        HasMoreRemoteEntries = snapshot.HasMore;
        RefreshRemoteGridEntries();

        if (snapshot.SelectedEntry is null)
        {
            SelectedRemoteEntry = null;
            _selectedRemoteEntries.Clear();
            EnqueueDownloadCommand.NotifyCanExecuteChanged();
            return;
        }

        SelectedRemoteEntry = RemoteGridEntries.FirstOrDefault(x =>
            string.Equals(x.Name, snapshot.SelectedEntry.Name, StringComparison.Ordinal) &&
            string.Equals(x.FullPath, snapshot.SelectedEntry.FullPath, StringComparison.OrdinalIgnoreCase));

        _selectedRemoteEntries.Clear();
        foreach (var selected in snapshot.SelectedEntries)
        {
            var mapped = RemoteGridEntries.FirstOrDefault(x =>
                string.Equals(x.Name, selected.Name, StringComparison.Ordinal) &&
                string.Equals(x.FullPath, selected.FullPath, StringComparison.OrdinalIgnoreCase));
            if (mapped is not null && mapped.Name != "..")
            {
                _selectedRemoteEntries.Add(mapped);
            }
        }

        EnqueueDownloadCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseLocalFolderAsync()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                InitialDirectory = Directory.Exists(LocalPath)
                    ? LocalPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Multiselect = false,
                Title = "Select Local Folder"
            };

            if (dialog.ShowDialog() == true)
            {
                LocalPath = dialog.FolderName;
                await LoadLocalDirectoryAsync();
            }
        }
        catch (Exception ex)
        {
            ShowError("Failed to browse for a local folder.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateLocalUp))]
    private async Task NavigateLocalUpAsync()
    {
        try
        {
            var parent = Directory.GetParent(NormalizeLocalPath(LocalPath))?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                return;
            }

            LocalPath = parent;
            await LoadLocalDirectoryAsync();
        }
        catch (Exception ex)
        {
            ShowError("Failed to navigate to local parent folder.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateLocalFolder))]
    private async Task CreateLocalFolderAsync()
    {
        try
        {
            var parent = NormalizeLocalPath(LocalPath);
            if (!Directory.Exists(parent))
            {
                return;
            }

            var folderName = BuildUniqueFolderName(
                candidate => Directory.Exists(Path.Combine(parent, candidate)) || File.Exists(Path.Combine(parent, candidate)),
                "New Folder");
            await _localFileOperationsService.CreateDirectoryAsync(parent, folderName, CancellationToken.None);
            await LoadLocalDirectoryAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied while creating local folder under {LocalPath}", LocalPath);
            QueueBatchStatusMessage = "Access denied while creating local folder.";
            OnPropertyChanged(nameof(StatusQueueText));
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "IO failure while creating local folder under {LocalPath}", LocalPath);
            QueueBatchStatusMessage = "Failed to create local folder due to an IO error.";
            OnPropertyChanged(nameof(StatusQueueText));
        }
        catch (Exception ex)
        {
            ShowError("Failed to create local folder.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateRemoteUp))]
    private async Task NavigateRemoteUpAsync()
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null || string.IsNullOrWhiteSpace(RemotePath))
        {
            return;
        }

        try
        {
            var normalized = NormalizeRemotePathDisplay(RemotePath);
            var parent = normalized.Contains('/')
                ? normalized[..normalized.LastIndexOf('/')]
                : string.Empty;

            var snapshot = CaptureRemoteViewSnapshot();
            var previousPath = RemotePath;
            RemotePath = parent;

            await ExecuteRemoteReadTaskAsync(
                operationType: RemoteOperationType.Browse,
                loadingMessage: "Loading parent remote folder...",
                clearGridFirst: false,
                showSpinnerAfterDelay: true,
                errorMessage: "Failed to open parent remote folder.",
                operation: token => LoadRemoteDirectoryPageAsync(reset: true, token, showUnexpectedErrorDialog: false),
                onFailureRollbackAsync: () =>
                {
                    RestoreRemoteViewSnapshot(snapshot);
                    RemotePath = previousPath;
                    return Task.CompletedTask;
                });
        }
        catch (Exception ex)
        {
            ShowError("Failed to navigate to remote parent folder.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateRemoteFolder))]
    private async Task CreateRemoteFolderAsync()
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        try
        {
            var folderName = BuildUniqueFolderName(
                candidate => RemoteEntries.Any(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase)),
                "New Folder");
            var relativePath = CombineRemotePath(RemotePath, folderName);
            await _remoteFileOperationsService.CreateDirectoryAsync(
                BuildSelectedSharePath(relativePath),
                CancellationToken.None);
            await LoadRemoteDirectoryAsync();
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            Log.Warning(
                ex,
                "Permission denied creating remote folder. Account={Account} Share={Share} Path={Path}",
                SelectedStorageAccount.Name,
                SelectedFileShare.Name,
                RemotePath);
            RemotePaneStatusMessage = "Permission denied while creating remote folder. Verify Azure Files data roles for this storage account.";
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            Log.Warning(
                ex,
                "Remote folder create conflict. Account={Account} Share={Share} Path={Path}",
                SelectedStorageAccount.Name,
                SelectedFileShare.Name,
                RemotePath);
            QueueBatchStatusMessage = "Remote folder already exists.";
            OnPropertyChanged(nameof(StatusQueueText));
        }
        catch (Exception ex)
        {
            ShowError("Failed to create remote folder.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnqueueUpload))]
    private async Task EnqueueUploadAsync()
    {
        if (SelectedLocalEntry is null || SelectedLocalEntry.Name == ".." || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var remoteRelative = string.IsNullOrWhiteSpace(RemotePath)
            ? SelectedLocalEntry.Name
            : $"{RemotePath.Trim('/')}/{SelectedLocalEntry.Name}";
        var request = CreateTransferRequest(
            TransferDirection.Upload,
            SelectedLocalEntry.FullPath,
            BuildSelectedSharePath(remoteRelative),
            UploadConflictDefaultPolicy);
        var batch = await ResolveAndEnqueueBatchAsync([request], startImmediately: true);
        SetQueueBatchStatus(batch);
        await PersistProfileAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEnqueueDownload))]
    private async Task EnqueueDownloadAsync()
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var selected = GetCurrentRemoteDownloadSelection();
        if (selected.Count == 0)
        {
            return;
        }

        var selectedRows = new ArrayList(selected.Count);
        foreach (var entry in selected)
        {
            selectedRows.Add(entry);
        }

        await QueueRemoteSelectionAsync(selectedRows, startImmediately: true);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelectedQueueItems))]
    private async Task PauseSelectedJobAsync()
    {
        await ApplyToSelectedQueueJobsAsync((jobId, token) => _transferQueueService.PauseAsync(jobId, token));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelectedQueueItems))]
    private async Task ResumeSelectedJobAsync()
    {
        await ApplyToSelectedQueueJobsAsync((jobId, token) => _transferQueueService.ResumeAsync(jobId, token));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelectedQueueItems))]
    private async Task RetrySelectedJobAsync()
    {
        await ApplyToSelectedQueueJobsAsync((jobId, token) => _transferQueueService.RetryAsync(jobId, token));
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelectedQueueItems))]
    private async Task CancelSelectedJobAsync()
    {
        await ApplyToSelectedQueueJobsAsync((jobId, token) => _transferQueueService.CancelAsync(jobId, token));
    }

    [RelayCommand]
    private async Task RunQueueAsync()
    {
        await _transferQueueService.RunQueuedAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task PauseAllQueueAsync()
    {
        await _transferQueueService.PauseAllAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanClearCompletedCanceledQueue))]
    private async Task ClearCompletedCanceledQueueAsync()
    {
        var removed = await _transferQueueService.PurgeAsync(
            [TransferJobStatus.Completed, TransferJobStatus.Canceled],
            CancellationToken.None);

        RefreshQueueItemsFromSnapshot();
        QueueBatchStatusMessage = removed > 0
            ? $"Cleared {removed} completed/canceled queue item(s)."
            : "No completed or canceled queue items to clear.";
        OnPropertyChanged(nameof(StatusQueueText));
    }

    [RelayCommand]
    private void ShowAllQueueFilters()
    {
        SelectedQueueStatusFilter = QueueFilterAll;
        SelectedQueueDirectionFilter = QueueFilterAll;
        QueueItemsView.Refresh();
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        await PersistProfileAsync();
        MessageBox.Show("Connection profile saved.", "Profile", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand(CanExecute = nameof(CanCopyRemoteDiagnostics))]
    private void CopyRemoteDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(RemoteDiagnosticsDetails))
        {
            return;
        }

        Clipboard.SetText(RemoteDiagnosticsDetails);
        QueueBatchStatusMessage = "Diagnostics copied to clipboard.";
        OnPropertyChanged(nameof(StatusQueueText));
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            _remoteOperationCoordinator.CancelCurrent(RemoteOperationCancelReason.SignOut);
            await _authenticationService.SignOutAsync(CancellationToken.None);
            LoginStatus = "Not signed in";
            Subscriptions.Clear();
            StorageAccounts.Clear();
            FileShares.Clear();
            ClearRemoteEntriesAndPaging();
            ClearRemoteSearchState(clearQuery: true);
            IsRemoteLoading = false;
            IsRemoteSpinnerVisible = false;
            RemoteLoadingMessage = string.Empty;
            ClearRemoteDiagnostics();
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Sign in to browse Azure storage roots."));
            _lastMirrorPlan = null;
            ExecuteMirrorCommand.NotifyCanExecuteChanged();
            Log.Information("User signed out.");
        }
        catch (Exception ex)
        {
            ShowError("Sign out failed.", ex);
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        if (!TransferSettingsWindow.TryShow(
                owner,
                TransferMaxConcurrency,
                TransferMaxKilobytesPerSecond,
                UploadConflictDefaultPolicy,
                DownloadConflictDefaultPolicy,
                UpdateChannel,
                out var newConcurrency,
                out var newThrottleKb,
                out var uploadConflictPolicy,
                out var downloadConflictPolicy,
                out var updateChannel))
        {
            return;
        }

        TransferMaxConcurrency = newConcurrency;
        TransferMaxKilobytesPerSecond = newThrottleKb;
        UploadConflictDefaultPolicy = uploadConflictPolicy;
        DownloadConflictDefaultPolicy = downloadConflictPolicy;
        UpdateChannel = updateChannel;
    }

    [RelayCommand]
    private void OpenHelp()
    {
        try
        {
            HelpWindow.Show(Application.Current?.MainWindow, _userHelpContentService);
        }
        catch (Exception ex)
        {
            ShowError("Failed to open help.", ex);
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0];
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informationalVersion;
        AboutWindow.Show(Application.Current?.MainWindow, version, () => CheckForUpdatesCommand.Execute(null));
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusMessage = "Updates: checking...";
            var check = await _appUpdateService.CheckForUpdatesAsync(CancellationToken.None);
            if (!check.IsUpdateAvailable)
            {
                UpdateStatusMessage = "Updates: no update available";
                MessageBox.Show(check.Message, "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var releaseVersion = check.LatestVersion ?? "unknown";
            var releaseUrl = check.ReleasePageUrl;
            var openPrompt = MessageBox.Show(
                $"New version {releaseVersion} is available (current: {check.CurrentVersion}).\n\nOpen the GitHub release page now?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (openPrompt != MessageBoxResult.Yes)
            {
                UpdateStatusMessage = "Updates: update available";
                return;
            }

            if (string.IsNullOrWhiteSpace(releaseUrl))
            {
                ErrorDialog.ShowMessage(
                    "Update Page Unavailable",
                    "No release URL was provided for the latest version.");
                UpdateStatusMessage = "Updates: failed";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = releaseUrl,
                UseShellExecute = true
            });
            UpdateStatusMessage = "Updates: release page opened";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = "Updates: failed";
            ShowError("Failed to check/open update page.", ex);
        }
    }

    [RelayCommand]
    private void ExitApplication()
    {
        Application.Current.Shutdown();
    }

    [RelayCommand(CanExecute = nameof(CanBuildMirrorPlan))]
    private async Task BuildMirrorPlanAsync()
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null || IsMirrorPlanning)
        {
            return;
        }

        var context = BuildRemoteContext();
        var capability = await _remoteCapabilityService.EvaluateAsync(context, CancellationToken.None);
        ApplyCapability(capability);
        if (!capability.CanPlanMirror)
        {
            MirrorPlanStatusMessage = "Mirror planning blocked: remote side is not accessible.";
            return;
        }

        var spec = new MirrorSpec(
            TransferDirection.Upload,
            LocalPath,
            BuildSelectedSharePath(RemotePath),
            IncludeDeletes);

        IsMirrorPlanning = true;
        MirrorPlanStatusMessage = "Planning mirror actions...";
        try
        {
            _lastMirrorPlan = await Task.Run(async () =>
                await _mirrorPlanner.BuildPlanAsync(spec, CancellationToken.None));
            ExecuteMirrorCommand.NotifyCanExecuteChanged();

            MirrorPlanStatusMessage =
                $"Mirror plan ready: {_lastMirrorPlan.CreateCount} create, {_lastMirrorPlan.UpdateCount} update, {_lastMirrorPlan.DeleteCount} delete";
            MessageBox.Show(MirrorPlanStatusMessage, "Mirror Plan", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (RequestFailedException ex)
        {
            _lastMirrorPlan = null;
            ExecuteMirrorCommand.NotifyCanExecuteChanged();
            var refreshed = await _remoteCapabilityService.RefreshAsync(context, CancellationToken.None);
            ApplyCapability(refreshed);
            MirrorPlanStatusMessage = "Mirror planning blocked by remote access capability.";
            Log.Warning(ex, "Mirror planning blocked due to remote capability state {State}", refreshed.State);
        }
        catch (Exception ex)
        {
            _lastMirrorPlan = null;
            ExecuteMirrorCommand.NotifyCanExecuteChanged();
            MirrorPlanStatusMessage = "Mirror planning failed.";
            ShowError("Failed to plan mirror actions.", ex);
        }
        finally
        {
            IsMirrorPlanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteMirror))]
    private async Task ExecuteMirrorAsync()
    {
        if (_lastMirrorPlan is null)
        {
            MessageBox.Show("Build a mirror plan before queueing mirror operations.", "Mirror Queue", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_lastMirrorPlan.DeleteCount > 0)
        {
            var confirm = MessageBox.Show(
                $"The mirror plan includes {_lastMirrorPlan.DeleteCount} delete operations. Continue?",
                "Delete Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await _mirrorExecution.ExecuteAsync(_lastMirrorPlan, CancellationToken.None);
    }

    partial void OnSelectedSubscriptionChanged(SubscriptionItem? value)
    {
        NavigateRemoteUpCommand.NotifyCanExecuteChanged();
        CreateRemoteFolderCommand.NotifyCanExecuteChanged();

        if (value is null || _isRestoringProfile || _suppressSelectionHandlers)
        {
            return;
        }

        _lastMirrorPlan = null;
        ExecuteMirrorCommand.NotifyCanExecuteChanged();

        StartSelectionChangeLoad(
            token => HandleSubscriptionSelectionChangedAsync(value, token),
            "Failed to load storage accounts for selected subscription.");
    }

    partial void OnSelectedStorageAccountChanged(StorageAccountItem? value)
    {
        _lastMirrorPlan = null;
        ExecuteMirrorCommand.NotifyCanExecuteChanged();
        NavigateRemoteUpCommand.NotifyCanExecuteChanged();
        CreateRemoteFolderCommand.NotifyCanExecuteChanged();
        SearchRemoteCommand.NotifyCanExecuteChanged();
        ClearRemoteSearchCommand.NotifyCanExecuteChanged();

        if (value is null || _isRestoringProfile || _suppressSelectionHandlers)
        {
            return;
        }

        StartSelectionChangeLoad(
            token => HandleStorageAccountSelectionChangedAsync(value, token),
            "Failed to load remote roots for selected storage account.");
    }

    partial void OnSelectedFileShareChanged(FileShareItem? value)
    {
        _lastMirrorPlan = null;
        ExecuteMirrorCommand.NotifyCanExecuteChanged();
        NavigateRemoteUpCommand.NotifyCanExecuteChanged();
        CreateRemoteFolderCommand.NotifyCanExecuteChanged();

        if (value is null || _isRestoringProfile || _suppressSelectionHandlers)
        {
            return;
        }

        StartSelectionChangeLoad(
            async token =>
            {
                token.ThrowIfCancellationRequested();
                RemotePath = string.Empty;
                await LoadRemoteDirectoryPageAsync(reset: true, token);
                token.ThrowIfCancellationRequested();
                await PersistProfileAsync();
            },
            "Failed to load remote directory for selected root.");
    }

    partial void OnSelectedLocalEntryChanged(LocalEntry? value)
    {
        if (value is null || value.Name == "..")
        {
            return;
        }

        _ = EnrichLocalEntryAsync(value);
    }

    partial void OnSelectedRemoteEntryChanged(RemoteEntry? value)
    {
        if (value is null || value.Name == "..")
        {
            if (_selectedRemoteEntries.Count > 0)
            {
                _selectedRemoteEntries.Clear();
            }

            EnqueueDownloadCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_selectedRemoteEntries.Count <= 1)
        {
            _selectedRemoteEntries.Clear();
            _selectedRemoteEntries.Add(value);
        }

        EnqueueDownloadCommand.NotifyCanExecuteChanged();

        if (_isUpdatingRemoteSelection || value is null || value.Name == ".." || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        _ = EnrichRemoteEntryAsync(value);
    }

    private async Task HandleSubscriptionSelectionChangedAsync(SubscriptionItem value, CancellationToken cancellationToken)
    {
        await LoadStorageAccountsAsync(value, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (SelectedStorageAccount is not null)
        {
            // Subscription changes can update SelectedStorageAccount silently, so load shares explicitly.
            await HandleStorageAccountSelectionChangedAsync(SelectedStorageAccount, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
        else
        {
            FileShares.Clear();
            ClearRemoteEntriesAndPaging();
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Select a storage account and remote root."));
        }

        await PersistProfileAsync();
    }

    private async Task HandleStorageAccountSelectionChangedAsync(StorageAccountItem value, CancellationToken cancellationToken)
    {
        SetSelectionSilently(() => SelectedFileShare = null);
        FileShares.Clear();
        ClearRemoteEntriesAndPaging();
        ClearRemoteDiagnostics();

        if (string.IsNullOrWhiteSpace(value.Name))
        {
            ClearRemoteSearchState(clearQuery: false);
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Selected storage account is missing a valid name."));
            LoginStatus = "Signed in. Selected storage account is missing a valid name.";
            return;
        }

        Log.Information("Storage account changed to {StorageAccount}. Resetting remote path.", value.Name);
        RemotePath = string.Empty;
        _lastSuccessfulRemotePath = string.Empty;
        ClearRemoteSearchState(clearQuery: false);
        await LoadFileSharesAsync(value, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await PersistProfileAsync();
    }

    private async Task LoadStorageAccountsAsync(SubscriptionItem subscription, CancellationToken cancellationToken)
    {
        Log.Debug("Loading storage accounts for subscription {SubscriptionId}", subscription.Id);
        var storageAccounts = new List<StorageAccountItem>();
        await foreach (var account in _azureDiscoveryService.ListStorageAccountsAsync(subscription.Id, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(account.Name))
            {
                storageAccounts.Add(account);
            }
        }

        ReplaceSortedCollection(StorageAccounts, storageAccounts, x => x.Name);

        if (SelectedStorageAccount is null || StorageAccounts.All(x => x.Name != SelectedStorageAccount.Name))
        {
            SetSelectionSilently(() => SelectedStorageAccount = StorageAccounts.FirstOrDefault());
        }

        Log.Debug("Loaded {StorageAccountCount} storage accounts.", StorageAccounts.Count);
    }

    private async Task LoadFileSharesAsync(StorageAccountItem account, CancellationToken cancellationToken)
    {
        Log.Debug("Loading remote roots (shares/containers) for storage account {StorageAccountName}", account.Name);
        var remoteRoots = new List<FileShareItem>();
        var fileEndpoint = BuildStorageFileEndpointHost(account.Name);
        var blobEndpoint = BuildStorageBlobEndpointHost(account.Name);
        var includeFileShares = !IsBlobOnlyStorageAccountKind(account.Kind);
        if (!includeFileShares)
        {
            Log.Debug(
                "Storage account {StorageAccountName} detected as blob-only kind ({StorageKind}); skipping Azure Files root discovery.",
                account.Name,
                account.Kind);
        }

        try
        {
            await foreach (var share in _azureDiscoveryService.ListFileSharesAsync(account.Name, includeFileShares, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(share.Name))
                {
                    remoteRoots.Add(share);
                }
            }
        }
        catch (Exception ex)
        {
            ClearRemoteEntriesAndPaging();
            ClearRemoteSearchState(clearQuery: false);
            var requestFailed = TryFindRequestFailedException(ex);
            if (IsDnsResolutionFailure(ex))
            {
                Log.Warning(ex, "Cannot resolve Azure endpoints for storage account {StorageAccountName}", account.Name);
                var message =
                    $"Cannot resolve or reach remote endpoints for '{account.Name}'. " +
                    "Verify private DNS/VPN routing and allow outbound HTTPS (443) through antivirus, proxy, and firewall rules for '*.file.core.windows.net' and '*.blob.core.windows.net'.";
                ApplyCapability(new RemoteCapabilitySnapshot(
                    RemoteAccessState.TransientFailure,
                    false,
                    false,
                    false,
                    false,
                    false,
                    message,
                    DateTimeOffset.UtcNow,
                    requestFailed?.ErrorCode,
                    requestFailed?.Status > 0 ? requestFailed.Status : null));
                LoginStatus = $"Signed in. Storage account '{account.Name}' is currently unreachable (DNS/network).";
                SetRemoteDiagnostics($"{fileEndpoint} | {blobEndpoint}", "DNS resolution failed while listing remote roots.", ex, requestFailed);
                return;
            }

            if (requestFailed is not null)
            {
                var capability = _remoteCapabilityService.GetLastKnown(BuildRemoteContext())
                    ?? new RemoteCapabilitySnapshot(
                        RemoteAccessState.Unknown,
                        false,
                        false,
                        false,
                        false,
                        false,
                        $"Cannot list remote roots for '{account.Name}' (HTTP {requestFailed.Status}).",
                        DateTimeOffset.UtcNow,
                        requestFailed.ErrorCode,
                        requestFailed.Status);
                ApplyCapability(capability with { UserMessage = $"Cannot list remote roots for '{account.Name}' (HTTP {requestFailed.Status}). Verify Azure Files/Blob data access." });
                LoginStatus = $"Signed in. Cannot list remote roots for '{account.Name}' ({requestFailed.Status}).";
                Log.Warning(ex, "Cannot list remote roots for storage account {StorageAccountName} due to Azure request failure.", account.Name);
                SetRemoteDiagnostics($"{fileEndpoint} | {blobEndpoint}", $"HTTP {requestFailed.Status} while listing remote roots.", ex, requestFailed);
                return;
            }

            Log.Warning(ex, "Cannot list remote roots for storage account {StorageAccountName} due to unexpected error.", account.Name);
            ApplyCapability(new RemoteCapabilitySnapshot(
                RemoteAccessState.TransientFailure,
                false,
                false,
                false,
                false,
                false,
                $"Cannot list remote roots for '{account.Name}'. Check connectivity and try Refresh.",
                DateTimeOffset.UtcNow));
            LoginStatus = $"Signed in. Cannot list remote roots for '{account.Name}' due to a connectivity error.";
            SetRemoteDiagnostics($"{fileEndpoint} | {blobEndpoint}", "Unexpected error while listing remote roots.", ex);
            return;
        }

        ReplaceSortedCollection(FileShares, remoteRoots, x => $"{x.Name}|{x.Kind}");
        ClearRemoteDiagnostics();

        if (SelectedFileShare is null || FileShares.All(x => x.Name != SelectedFileShare.Name || x.Kind != SelectedFileShare.Kind))
        {
            SetSelectionSilently(() => SelectedFileShare = FileShares.FirstOrDefault());
        }

        var shareCount = FileShares.Count(x => x.Kind == RemoteRootKind.FileShare);
        var containerCount = FileShares.Count(x => x.Kind == RemoteRootKind.BlobContainer);
        Log.Debug("Loaded {ShareCount} file shares and {ContainerCount} blob containers for {StorageAccountName}", shareCount, containerCount, account.Name);

        cancellationToken.ThrowIfCancellationRequested();
        await LoadRemoteDirectoryPageAsync(reset: true, cancellationToken);
    }

    private static RequestFailedException? TryFindRequestFailedException(Exception ex)
    {
        if (ex is RequestFailedException direct)
        {
            return direct;
        }

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                if (inner is RequestFailedException requestFailed)
                {
                    return requestFailed;
                }
            }
        }

        return ex.InnerException is null ? null : TryFindRequestFailedException(ex.InnerException);
    }

    private static bool IsDnsResolutionFailure(Exception ex)
    {
        if (ex is SocketException socket &&
            socket.SocketErrorCode == SocketError.HostNotFound)
        {
            return true;
        }

        if (ex is HttpRequestException http && http.InnerException is not null)
        {
            return IsDnsResolutionFailure(http.InnerException);
        }

        if (ex is RequestFailedException requestFailed)
        {
            if (requestFailed.InnerException is not null && IsDnsResolutionFailure(requestFailed.InnerException))
            {
                return true;
            }

            return requestFailed.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase);
        }

        if (ex is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(IsDnsResolutionFailure);
        }

        return ex.InnerException is not null && IsDnsResolutionFailure(ex.InnerException);
    }

    private static string BuildStorageFileEndpointHost(string storageAccountName) =>
        $"{storageAccountName}.file.core.windows.net";

    private static string BuildStorageBlobEndpointHost(string storageAccountName) =>
        $"{storageAccountName}.blob.core.windows.net";

    private static bool IsBlobOnlyStorageAccountKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        return string.Equals(kind, "BlobStorage", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "BlockBlobStorage", StringComparison.OrdinalIgnoreCase);
    }

    private void SetRemoteDiagnostics(string endpointHost, string summary, Exception? ex = null, RequestFailedException? requestFailed = null)
    {
        var details = new List<string>
        {
            $"TimestampUtc: {DateTimeOffset.UtcNow:O}",
            $"EndpointHost: {endpointHost}",
            $"Summary: {summary}"
        };

        if (requestFailed is not null)
        {
            details.Add($"HttpStatus: {requestFailed.Status}");
            if (!string.IsNullOrWhiteSpace(requestFailed.ErrorCode))
            {
                details.Add($"ErrorCode: {requestFailed.ErrorCode}");
            }
        }

        if (ex is not null)
        {
            details.Add($"ExceptionType: {ex.GetType().FullName}");
            details.Add($"ExceptionMessage: {ex.Message}");
        }

        RemoteDiagnosticsDetails = string.Join(Environment.NewLine, details);
    }

    private void ClearRemoteDiagnostics()
    {
        RemoteDiagnosticsDetails = string.Empty;
    }

    private async Task LoadLocalProfileDefaultsAsync()
    {
        var profile = await _connectionProfileStore.LoadAsync(CancellationToken.None);
        _isRestoringProfile = true;
        try
        {
            LocalPath = NormalizeLocalPath(profile.LocalPath);
            RemotePath = NormalizeRemotePathDisplay(profile.RemotePath);
            _lastSuccessfulRemotePath = RemotePath;
            IncludeDeletes = profile.IncludeDeletes;
            TransferMaxConcurrency = NormalizeTransferConcurrency(profile.TransferMaxConcurrency);
            TransferMaxBytesPerSecond = NormalizeTransferMaxBytesPerSecond(profile.TransferMaxBytesPerSecond);
            UploadConflictDefaultPolicy = NormalizeConflictPolicy(profile.UploadConflictDefaultPolicy);
            DownloadConflictDefaultPolicy = NormalizeConflictPolicy(profile.DownloadConflictDefaultPolicy);
            UpdateChannel = profile.UpdateChannel;
            LocalGridLayout = profile.LocalGridLayout;
            RemoteGridLayout = profile.RemoteGridLayout;
            ReplaceCollection(RecentLocalPaths, profile.RecentLocalPaths, includeIfEmpty: LocalPath);
            ReplaceCollection(RecentRemotePaths, profile.RecentRemotePaths);
        }
        finally
        {
            _isRestoringProfile = false;
        }

        await LoadLocalDirectoryAsync();
    }

    private async Task ApplyProfileSelectionsAsync()
    {
        var profile = await _connectionProfileStore.LoadAsync(CancellationToken.None);
        _isRestoringProfile = true;
        try
        {
            ReplaceCollection(RecentLocalPaths, profile.RecentLocalPaths, includeIfEmpty: LocalPath);
            ReplaceCollection(RecentRemotePaths, profile.RecentRemotePaths);

            var subscription = Subscriptions.FirstOrDefault(x => string.Equals(x.Id, profile.SubscriptionId, StringComparison.OrdinalIgnoreCase))
                ?? Subscriptions.FirstOrDefault();

            if (subscription is null)
            {
                return;
            }

            SelectedSubscription = subscription;
            await LoadStorageAccountsAsync(subscription, CancellationToken.None);

            var account = StorageAccounts.FirstOrDefault(x => string.Equals(x.Name, profile.StorageAccountName, StringComparison.OrdinalIgnoreCase))
                ?? StorageAccounts.FirstOrDefault();
            if (account is null)
            {
                return;
            }

            SelectedStorageAccount = account;
            await LoadFileSharesAsync(account, CancellationToken.None);

            var namedRoots = FileShares
                .Where(x => string.Equals(x.Name, profile.FileShareName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            FileShareItem? share = null;
            if (namedRoots.Count > 0)
            {
                var preferredKind = profile.RemoteRootKind ?? RemoteRootKind.FileShare;
                // Legacy profiles did not persist RemoteRootKind, and were Azure Files-only.
                share = namedRoots.FirstOrDefault(x => x.Kind == preferredKind)
                    ?? namedRoots.FirstOrDefault();
            }

            if (share is not null)
            {
                SelectedFileShare = share;
            }
        }
        finally
        {
            _isRestoringProfile = false;
        }

        await PersistProfileAsync();
    }

    private async Task PersistProfileAsync()
    {
        if (_isRestoringProfile)
        {
            return;
        }

        var profile = new ConnectionProfile(
            SelectedSubscription?.Id,
            SelectedStorageAccount?.Name,
            SelectedFileShare?.Name,
            NormalizeLocalPath(LocalPath),
            RemotePath,
            IncludeDeletes,
            TransferMaxConcurrency,
            TransferMaxBytesPerSecond,
            UploadConflictDefaultPolicy,
            DownloadConflictDefaultPolicy,
            RecentLocalPaths.ToList(),
            RecentRemotePaths.ToList(),
            LocalGridLayout,
            RemoteGridLayout,
            UpdateChannel,
            SelectedFileShare?.Kind);

        await _connectionProfileStore.SaveAsync(profile, CancellationToken.None);
    }

    public async Task QueueLocalSelectionAsync(IList selectedRows, bool startImmediately)
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var selected = selectedRows.Cast<object>().OfType<LocalEntry>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0)
        {
            return;
        }

        Log.Debug(
            "Queueing local selection. SelectionCount={SelectionCount} StartImmediately={StartImmediately}",
            selected.Count,
            startImmediately);

        var requests = new List<TransferRequest>();
        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
            {
                var remotePrefix = CombineRemotePath(RemotePath, entry.Name);
                foreach (var filePath in Directory.EnumerateFiles(entry.FullPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(entry.FullPath, filePath).Replace('\\', '/');
                    var remoteRelative = CombineRemotePath(remotePrefix, relative);
                    requests.Add(CreateTransferRequest(
                        TransferDirection.Upload,
                        filePath,
                        BuildSelectedSharePath(remoteRelative),
                        UploadConflictDefaultPolicy));
                }
            }
            else
            {
                var remoteRelative = CombineRemotePath(RemotePath, entry.Name);
                requests.Add(CreateTransferRequest(
                    TransferDirection.Upload,
                    entry.FullPath,
                    BuildSelectedSharePath(remoteRelative),
                    UploadConflictDefaultPolicy));
            }
        }

        var batch = await ResolveAndEnqueueBatchAsync(requests, startImmediately);
        SetQueueBatchStatus(batch);
        await PersistProfileAsync();
    }

    public async Task QueueRemoteSelectionAsync(IList selectedRows, bool startImmediately)
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var selected = selectedRows.Cast<object>().OfType<RemoteEntry>().Where(x => x.Name != "..").ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var normalizedLocalPath = NormalizeLocalPath(LocalPath);
        LocalPath = normalizedLocalPath;

        Log.Debug(
            "Queueing remote selection. SelectionCount={SelectionCount} StartImmediately={StartImmediately}",
            selected.Count,
            startImmediately);

        var requests = new List<TransferRequest>();
        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
            {
                var files = await ListRemoteFilesRecursivelyAsync(entry.FullPath, CancellationToken.None);
                foreach (var file in files)
                {
                    var relativeUnderFolder = file.FullPath[entry.FullPath.Length..].TrimStart('/');
                    var localTarget = Path.Combine(normalizedLocalPath, entry.Name, relativeUnderFolder.Replace('/', Path.DirectorySeparatorChar));
                    requests.Add(CreateTransferRequest(
                        TransferDirection.Download,
                        localTarget,
                        BuildSelectedSharePath(file.FullPath),
                        DownloadConflictDefaultPolicy));
                }
            }
            else
            {
                var localTarget = Path.Combine(normalizedLocalPath, entry.Name);
                requests.Add(CreateTransferRequest(
                    TransferDirection.Download,
                    localTarget,
                    BuildSelectedSharePath(entry.FullPath),
                    DownloadConflictDefaultPolicy));
            }
        }

        var batch = await ResolveAndEnqueueBatchAsync(requests, startImmediately);
        SetQueueBatchStatus(batch);
        await PersistProfileAsync();
    }

    public async Task ShowInExplorerAsync(LocalEntry? entry)
    {
        if (entry is null || entry.Name == "..")
        {
            return;
        }

        await _localFileOperationsService.ShowInExplorerAsync(entry.FullPath, CancellationToken.None);
    }

    public async Task OpenLocalAsync(LocalEntry? entry)
    {
        if (entry is null || entry.Name == "..")
        {
            return;
        }

        await _localFileOperationsService.OpenAsync(entry.FullPath, CancellationToken.None);
    }

    public async Task OpenLocalWithAsync(LocalEntry? entry)
    {
        if (entry is null || entry.Name == "..")
        {
            return;
        }

        await _localFileOperationsService.OpenWithAsync(entry.FullPath, CancellationToken.None);
    }

    public async Task RenameLocalAsync(LocalEntry? entry, string newName)
    {
        if (entry is null || entry.Name == ".." || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        await _localFileOperationsService.RenameAsync(entry.FullPath, newName.Trim(), CancellationToken.None);
        await LoadLocalDirectoryAsync();
    }

    public async Task DeleteLocalAsync(LocalEntry? entry, bool recursive)
    {
        if (entry is null || entry.Name == "..")
        {
            return;
        }

        await _localFileOperationsService.DeleteAsync(entry.FullPath, recursive, CancellationToken.None);
        await LoadLocalDirectoryAsync();
    }

    public async Task<DeleteBatchResult> DeleteLocalSelectionAsync(IList selectedRows, bool recursive)
    {
        var targets = selectedRows.Cast<object>()
            .OfType<LocalEntry>()
            .Where(x => x.Name != "..")
            .GroupBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (targets.Count == 0)
        {
            var empty = new DeleteBatchResult(0, 0, 0);
            SetDeleteBatchStatus("local", empty);
            return empty;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var target in targets)
        {
            try
            {
                await _localFileOperationsService.DeleteAsync(target.FullPath, recursive, CancellationToken.None);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning(ex, "Failed to delete local entry {LocalPath}", target.FullPath);
            }
        }

        if (deleted > 0)
        {
            await LoadLocalDirectoryAsync();
        }

        var result = new DeleteBatchResult(targets.Count, deleted, failed);
        SetDeleteBatchStatus("local", result);
        return result;
    }

    public async Task RenameRemoteAsync(RemoteEntry? entry, string newName)
    {
        if (entry is null || entry.Name == ".." || string.IsNullOrWhiteSpace(newName) || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        await _remoteFileOperationsService.RenameAsync(
            BuildSelectedSharePath(entry.FullPath),
            newName.Trim(),
            CancellationToken.None);
        await LoadRemoteDirectoryAsync();
    }

    public async Task DeleteRemoteAsync(RemoteEntry? entry, bool recursive)
    {
        if (entry is null || entry.Name == ".." || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        await _remoteFileOperationsService.DeleteAsync(
            BuildSelectedSharePath(entry.FullPath),
            recursive,
            CancellationToken.None);
        await LoadRemoteDirectoryAsync();
    }

    public async Task<DeleteBatchResult> DeleteRemoteSelectionAsync(IList selectedRows, bool recursive)
    {
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            var noSelection = new DeleteBatchResult(0, 0, 0);
            SetDeleteBatchStatus("remote", noSelection);
            return noSelection;
        }

        var targets = selectedRows.Cast<object>()
            .OfType<RemoteEntry>()
            .Where(x => x.Name != "..")
            .GroupBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (targets.Count == 0)
        {
            var empty = new DeleteBatchResult(0, 0, 0);
            SetDeleteBatchStatus("remote", empty);
            return empty;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var target in targets)
        {
            try
            {
                await _remoteFileOperationsService.DeleteAsync(
                    BuildSelectedSharePath(target.FullPath),
                    recursive,
                    CancellationToken.None);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning(ex, "Failed to delete remote entry {RemotePath}", target.FullPath);
            }
        }

        if (deleted > 0)
        {
            await LoadRemoteDirectoryAsync();
        }

        var result = new DeleteBatchResult(targets.Count, deleted, failed);
        SetDeleteBatchStatus("remote", result);
        return result;
    }

    public async Task OpenLocalEntryAsync(LocalEntry? entry)
    {
        if (entry is null || !entry.IsDirectory)
        {
            return;
        }

        LocalPath = entry.FullPath;
        await LoadLocalDirectoryAsync();
    }

    public async Task OpenRemoteEntryAsync(RemoteEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (IsRemoteSearchActive)
        {
            await GoToRemoteEntryLocationAsync(entry);
            return;
        }

        if (!entry.IsDirectory)
        {
            await OpenRemoteForEditingAsync(entry);
            return;
        }

        Log.Debug("Opening remote directory by double-click. Name={Name} Path={Path}", entry.Name, entry.FullPath);
        var snapshot = CaptureRemoteViewSnapshot();
        var previousPath = RemotePath;
        RemotePath = entry.FullPath;

        await ExecuteRemoteReadTaskAsync(
            operationType: RemoteOperationType.Browse,
            loadingMessage: $"Opening '{entry.Name}'...",
            clearGridFirst: false,
            showSpinnerAfterDelay: true,
            errorMessage: "Failed to open remote folder.",
            operation: token => LoadRemoteDirectoryPageAsync(reset: true, token, showUnexpectedErrorDialog: false),
            onFailureRollbackAsync: () =>
            {
                RestoreRemoteViewSnapshot(snapshot);
                RemotePath = previousPath;
                return Task.CompletedTask;
            },
            operationTimeout: _remoteOpenDirectoryTimeout,
            timeoutMessage: "Opening remote folder timed out. View restored to previous path.");
    }


    public async Task OpenRemoteForEditingAsync(RemoteEntry? entry)
    {
        if (entry is null || entry.Name == ".." || entry.IsDirectory || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var path = BuildSelectedSharePath(entry.FullPath);
        await _remoteEditSessionService.OpenAsync(path, entry.Name, CancellationToken.None);
    }

    public Task<IReadOnlyList<RemoteEditPendingChange>> GetPendingRemoteEditChangesAsync() =>
        _remoteEditSessionService.GetPendingChangesAsync(CancellationToken.None);

    public Task<RemoteEditSyncResult> SyncRemoteEditAsync(Guid sessionId, bool overwriteIfRemoteChanged) =>
        _remoteEditSessionService.SyncAsync(sessionId, overwriteIfRemoteChanged, CancellationToken.None);

    public Task<bool> DiscardRemoteEditAsync(Guid sessionId) =>
        _remoteEditSessionService.DiscardAsync(sessionId, CancellationToken.None);
    public async Task GoToRemoteEntryLocationAsync(RemoteEntry? entry)
    {
        if (entry is null || SelectedStorageAccount is null || SelectedFileShare is null || entry.Name == "..")
        {
            return;
        }

        var normalized = entry.FullPath.Replace('\\', '/').Trim('/');
        var parentPath = normalized.Contains('/')
            ? normalized[..normalized.LastIndexOf('/')]
            : string.Empty;

        var previousPath = RemotePath;
        var previousSearchStatus = RemoteSearchStatusMessage;
        var previousQuery = RemoteSearchQuery;
        var previousScope = SelectedRemoteSearchScope;
        var previousSearchActive = IsRemoteSearchActive;
        RemotePath = parentPath;
        ClearRemoteSearchState(clearQuery: false);

        var success = await ExecuteRemoteReadTaskAsync(
            operationType: RemoteOperationType.Browse,
            loadingMessage: $"Opening '{entry.Name}'...",
            clearGridFirst: false,
            showSpinnerAfterDelay: true,
            errorMessage: "Failed to open remote folder.",
            operation: token => LoadRemoteDirectoryPageAsync(reset: true, token, showUnexpectedErrorDialog: false),
            onFailureRollbackAsync: () =>
            {
                RemotePath = previousPath;
                IsRemoteSearchActive = previousSearchActive;
                RemoteSearchQuery = previousQuery;
                SelectedRemoteSearchScope = previousScope;
                RemoteSearchStatusMessage = previousSearchStatus;
                return Task.CompletedTask;
            },
            operationTimeout: _remoteOpenDirectoryTimeout,
            timeoutMessage: "Opening remote folder timed out. View restored to previous path.");

        if (!success)
        {
            return;
        }

        var target = RemoteGridEntries.FirstOrDefault(x =>
            x.Name != ".." &&
            string.Equals(x.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            SelectedRemoteEntry = target;
            _selectedRemoteEntries.Clear();
            _selectedRemoteEntries.Add(target);
            EnqueueDownloadCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task UpdateGridLayoutsAsync(GridLayoutProfile? local, GridLayoutProfile? remote)
    {
        LocalGridLayout = local;
        RemoteGridLayout = remote;
        await PersistProfileAsync();
    }

    private static void AddRecentPath(ObservableCollection<string> target, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = path.Trim();
        if (target.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            var existing = target.First(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
            target.Remove(existing);
        }

        target.Insert(0, normalized);
        while (target.Count > 10)
        {
            target.Remove(target.Last());
        }
    }

    private static void ReplaceCollection(ObservableCollection<string> target, IEnumerable<string> values, string? includeIfEmpty = null)
    {
        target.Clear();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10))
        {
            target.Add(value);
        }

        if (!string.IsNullOrWhiteSpace(includeIfEmpty) && target.Count == 0)
        {
            target.Add(includeIfEmpty);
        }
    }

    private static void ReplaceSortedCollection<TItem>(
        ObservableCollection<TItem> target,
        IEnumerable<TItem> values,
        Func<TItem, string> orderBy)
    {
        target.Clear();
        foreach (var item in values.OrderBy(orderBy, StringComparer.CurrentCultureIgnoreCase))
        {
            target.Add(item);
        }
    }

    private void OnJobUpdated(object? sender, TransferJobSnapshot e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = QueueItems.FirstOrDefault(x => x.JobId == e.JobId);
            var view = new QueueItemView { Snapshot = e };
            if (existing is null)
            {
                QueueItems.Insert(0, view);
                ClearCompletedCanceledQueueCommand.NotifyCanExecuteChanged();
                return;
            }

            var index = QueueItems.IndexOf(existing);
            QueueItems[index] = view;

            if (SelectedQueueItem?.JobId == e.JobId)
            {
                SelectedQueueItem = view;
            }

            if (e.Status == TransferJobStatus.Completed)
            {
                _ = RefreshPaneAfterCompletedTransferAsync(e);
            }

            ClearCompletedCanceledQueueCommand.NotifyCanExecuteChanged();
        });
    }

    private void RefreshQueueItemsFromSnapshot()
    {
        var selectedIds = SelectedQueueJobIds.ToHashSet();
        var snapshots = _transferQueueService.Snapshot();

        QueueItems.Clear();
        foreach (var snapshot in snapshots)
        {
            QueueItems.Add(new QueueItemView { Snapshot = snapshot });
        }

        SelectedQueueJobIds.Clear();
        foreach (var jobId in selectedIds.Where(id => QueueItems.Any(x => x.JobId == id)))
        {
            SelectedQueueJobIds.Add(jobId);
        }

        SelectedQueueCount = SelectedQueueJobIds.Count;
        QueueItemsView.Refresh();
        ClearCompletedCanceledQueueCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshPaneAfterCompletedTransferAsync(TransferJobSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (snapshot.Request.Direction == TransferDirection.Upload)
            {
                if (now - _lastRemoteRefreshUtc < TimeSpan.FromMilliseconds(750))
                {
                    return;
                }

                _lastRemoteRefreshUtc = now;
                await LoadRemoteDirectoryAsync();
            }
            else
            {
                if (now - _lastLocalRefreshUtc < TimeSpan.FromMilliseconds(750))
                {
                    return;
                }

                _lastLocalRefreshUtc = now;
                await LoadLocalDirectoryAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Post-transfer pane refresh failed.");
        }
    }

    private void ApplyCapability(RemoteCapabilitySnapshot snapshot)
    {
        RemoteCapability = snapshot;
        RemotePaneStatusMessage = snapshot.State == RemoteAccessState.Accessible ? string.Empty : snapshot.UserMessage;
        if (string.IsNullOrWhiteSpace(RemotePaneStatusMessage))
        {
            ClearRemoteDiagnostics();
        }
        EnqueueUploadCommand.NotifyCanExecuteChanged();
        EnqueueDownloadCommand.NotifyCanExecuteChanged();
        BuildMirrorPlanCommand.NotifyCanExecuteChanged();
        ExecuteMirrorCommand.NotifyCanExecuteChanged();
        NavigateRemoteUpCommand.NotifyCanExecuteChanged();
        CreateRemoteFolderCommand.NotifyCanExecuteChanged();
    }

    private RemoteContext BuildRemoteContext() =>
        new(
            SelectedStorageAccount?.Name ?? string.Empty,
            SelectedFileShare?.Name ?? string.Empty,
            RemotePath,
            SelectedSubscription?.Id,
            SelectedFileShare?.ProviderKind ?? RemoteProviderKind.AzureFiles);

    private RemoteActionPolicy BuildRemotePolicy()
    {
        var inputs = new RemoteActionInputs(
            HasSelectedLocalFile: SelectedLocalEntry is { IsDirectory: false },
            HasSelectedRemoteFile: HasDownloadableRemoteSelection(),
            HasMirrorPlan: _lastMirrorPlan is not null,
            IsMirrorPlanning: IsMirrorPlanning);
        return _remoteActionPolicyService.Compute(RemoteCapability, inputs);
    }

    private static void ShowError(string summary, Exception ex)
    {
        ErrorDialog.Show(summary, ex);
    }

    private string NormalizeLocalPath(string? path) => _pathDisplayFormatter.NormalizeLocalPath(path, _localPathFallbackRoot);

    private string NormalizeRemotePathDisplay(string? value) => _pathDisplayFormatter.NormalizeRemotePathDisplay(value);

    private string FormatRemotePathDisplay(string? path) => _pathDisplayFormatter.FormatRemotePathDisplay(path);

    private static IReadOnlyList<string> BuildRemotePathFallbackCandidates(string requestedPath, string previousPath)
    {
        var candidates = new List<string>(3) { requestedPath };

        if (!string.Equals(previousPath, requestedPath, StringComparison.Ordinal))
        {
            candidates.Add(previousPath);
        }

        if (!string.Equals(string.Empty, requestedPath, StringComparison.Ordinal) &&
            !string.Equals(string.Empty, previousPath, StringComparison.Ordinal))
        {
            candidates.Add(string.Empty);
        }

        return candidates;
    }

    private void OnRecentRemotePathsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RecentRemotePathDisplayOptions));
    }

    private bool CanBuildMirrorPlan() => BuildRemotePolicy().CanPlanMirror;
    private bool CanExecuteMirror() => BuildRemotePolicy().CanExecuteMirror;
    private bool CanEnqueueUpload() => BuildRemotePolicy().CanEnqueueUpload;
    private bool CanEnqueueDownload() => BuildRemotePolicy().CanEnqueueDownload;
    private bool CanNavigateLocalUp() => !string.IsNullOrWhiteSpace(Directory.GetParent(NormalizeLocalPath(LocalPath))?.FullName);
    private bool CanCreateLocalFolder() => Directory.Exists(NormalizeLocalPath(LocalPath));
    private bool CanNavigateRemoteUp() =>
        !IsRemoteLoading &&
        !string.IsNullOrWhiteSpace(RemotePath) &&
        SelectedStorageAccount is not null &&
        SelectedFileShare is not null &&
        (RemoteCapability?.CanBrowse ?? false);
    private bool CanCreateRemoteFolder() =>
        !IsRemoteLoading &&
        SelectedStorageAccount is not null &&
        SelectedFileShare is not null &&
        (RemoteCapability?.CanUpload ?? false);
    private bool CanLoadMoreRemoteEntries() =>
        HasMoreRemoteEntries &&
        !IsRemoteSearchActive &&
        !IsRemoteLoading &&
        SelectedStorageAccount is not null &&
        SelectedFileShare is not null &&
        (RemoteCapability?.CanBrowse ?? false);
    private bool CanSearchRemote() =>
        !IsRemoteLoading &&
        SelectedStorageAccount is not null &&
        SelectedFileShare is not null &&
        !string.IsNullOrWhiteSpace(RemoteSearchQuery) &&
        (RemoteCapability?.CanBrowse ?? true);
    private bool CanClearRemoteSearch() => IsRemoteSearchActive && !IsRemoteLoading;
    private bool CanCancelRemoteSearch() => IsRemoteSearchActive && IsRemoteLoading;
    private bool CanActOnSelectedQueueItems() => SelectedQueueCount > 0;
    private bool CanClearCompletedCanceledQueue() => QueueItems.Any(x => x.Status is TransferJobStatus.Completed or TransferJobStatus.Canceled);
    private bool CanCopyRemoteDiagnostics() => !string.IsNullOrWhiteSpace(RemoteDiagnosticsDetails);

    partial void OnTransferMaxConcurrencyChanged(int value)
    {
        var normalized = NormalizeTransferConcurrency(value);
        if (value != normalized)
        {
            TransferMaxConcurrency = normalized;
            return;
        }

        OnPropertyChanged(nameof(StatusConcurrencyText));
        _ = PersistProfileAsync();
    }

    partial void OnTransferMaxBytesPerSecondChanged(int value)
    {
        var normalized = NormalizeTransferMaxBytesPerSecond(value);
        if (value != normalized)
        {
            TransferMaxBytesPerSecond = normalized;
            return;
        }

        OnPropertyChanged(nameof(TransferMaxKilobytesPerSecond));
        OnPropertyChanged(nameof(StatusThrottleText));
        _ = PersistProfileAsync();
    }

    partial void OnUploadConflictDefaultPolicyChanged(TransferConflictPolicy value)
    {
        _ = PersistProfileAsync();
    }

    partial void OnDownloadConflictDefaultPolicyChanged(TransferConflictPolicy value)
    {
        _ = PersistProfileAsync();
    }

    partial void OnUpdateChannelChanged(UpdateChannel value)
    {
        _appUpdateService.SetChannel(value);
        _ = PersistProfileAsync();
    }

    partial void OnLocalPathChanged(string value)
    {
        NotifyCanExecuteChangedSafe(() => NavigateLocalUpCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => CreateLocalFolderCommand.NotifyCanExecuteChanged());
    }

    partial void OnRemotePathChanged(string value)
    {
        Log.Debug("Remote path changed. Path={RemotePath}", value);
        OnPropertyChanged(nameof(RemotePathDisplay));
        NotifyCanExecuteChangedSafe(() => NavigateRemoteUpCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => CreateRemoteFolderCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => SearchRemoteCommand.NotifyCanExecuteChanged());
    }

    partial void OnIsRemoteLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRemoteGridEnabled));
        OnPropertyChanged(nameof(IsRemoteLoadingOverlayVisible));
        NotifyCanExecuteChangedSafe(() => LoadMoreRemoteEntriesCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => NavigateRemoteUpCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => CreateRemoteFolderCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => SearchRemoteCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => ClearRemoteSearchCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => CancelRemoteSearchCommand.NotifyCanExecuteChanged());
    }

    partial void OnIsRemoteSearchActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRemoteLoadingOverlayVisible));
        NotifyCanExecuteChangedSafe(() => CancelRemoteSearchCommand.NotifyCanExecuteChanged());
    }

    partial void OnHasMoreRemoteEntriesChanged(bool value)
    {
        NotifyCanExecuteChangedSafe(() => LoadMoreRemoteEntriesCommand.NotifyCanExecuteChanged());
    }

    partial void OnLoginStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusThrottleText));
        OnPropertyChanged(nameof(StatusConcurrencyText));
        OnPropertyChanged(nameof(StatusQueueText));
    }

    partial void OnSelectedQueueCountChanged(int value)
    {
        NotifyCanExecuteChangedSafe(() => PauseSelectedJobCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => ResumeSelectedJobCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => RetrySelectedJobCommand.NotifyCanExecuteChanged());
        NotifyCanExecuteChangedSafe(() => CancelSelectedJobCommand.NotifyCanExecuteChanged());
        OnPropertyChanged(nameof(SelectedQueueCountText));
    }

    partial void OnSelectedQueueStatusFilterChanged(string value)
    {
        QueueItemsView.Refresh();
    }

    partial void OnSelectedQueueDirectionFilterChanged(string value)
    {
        QueueItemsView.Refresh();
    }

    private static void NotifyCanExecuteChangedSafe(Action notify)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            notify();
            return;
        }

        _ = dispatcher.InvokeAsync(notify, DispatcherPriority.Background);
    }

    public string SelectedQueueCountText => SelectedQueueCount > 0 ? $"Selected: {SelectedQueueCount}" : string.Empty;

    public void UpdateSelectedQueueSelection(IList selectedRows)
    {
        SelectedQueueJobIds.Clear();
        foreach (var id in selectedRows.Cast<object>().OfType<QueueItemView>().Select(x => x.JobId).Distinct())
        {
            SelectedQueueJobIds.Add(id);
        }

        SelectedQueueCount = SelectedQueueJobIds.Count;
    }

    public void UpdateSelectedRemoteSelection(IList selectedRows)
    {
        _selectedRemoteEntries.Clear();
        foreach (var entry in selectedRows.Cast<object>().OfType<RemoteEntry>())
        {
            if (entry.Name == "..")
            {
                continue;
            }

            if (_selectedRemoteEntries.Any(x =>
                    string.Equals(x.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, entry.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            _selectedRemoteEntries.Add(entry);
        }

        NotifyCanExecuteChangedSafe(() => EnqueueDownloadCommand.NotifyCanExecuteChanged());
    }

    private bool HasDownloadableRemoteSelection()
    {
        if (IsRemoteLoading)
        {
            return false;
        }

        if (_selectedRemoteEntries.Count > 0)
        {
            return _selectedRemoteEntries.Any(x => x.Name != "..");
        }

        return SelectedRemoteEntry is { Name: not ".." };
    }

    private List<RemoteEntry> GetCurrentRemoteDownloadSelection()
    {
        if (_selectedRemoteEntries.Count > 0)
        {
            return [.. _selectedRemoteEntries];
        }

        if (SelectedRemoteEntry is not null && SelectedRemoteEntry.Name != "..")
        {
            return [SelectedRemoteEntry];
        }

        return [];
    }

    private async Task ApplyToSelectedQueueJobsAsync(Func<Guid, CancellationToken, Task> operation)
    {
        var selectedIds = SelectedQueueJobIds.ToList();
        if (selectedIds.Count == 0)
        {
            return;
        }

        foreach (var jobId in selectedIds)
        {
            await operation(jobId, CancellationToken.None);
        }
    }

    private bool ShouldIncludeQueueItem(object item)
    {
        if (item is not QueueItemView queueItem)
        {
            return false;
        }

        if (!string.Equals(SelectedQueueStatusFilter, QueueFilterAll, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(queueItem.Status.ToString(), SelectedQueueStatusFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(SelectedQueueDirectionFilter, QueueFilterAll, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(queueItem.Request.Direction.ToString(), SelectedQueueDirectionFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void StartSelectionChangeLoad(Func<CancellationToken, Task> operation, string errorMessage)
    {
        _remoteOperationCoordinator.CancelCurrent(RemoteOperationCancelReason.SelectionChanged);
        _ = RunAsync();

        async Task RunAsync()
        {
            ClearRemoteSearchState(clearQuery: false);
            await ExecuteRemoteReadTaskAsync(
                operationType: RemoteOperationType.SelectionChange,
                loadingMessage: "Loading remote context...",
                clearGridFirst: true,
                showSpinnerAfterDelay: false,
                errorMessage: errorMessage,
                operation: async token =>
                {
                    await operation(token);
                    return true;
                },
                onFailureRollbackAsync: null);
        }
    }

    private void SetSelectionSilently(Action setSelection)
    {
        _suppressSelectionHandlers = true;
        try
        {
            setSelection();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private static string CombineRemotePath(params string[] parts)
    {
        var normalized = parts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Replace('\\', '/').Trim('/'))
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join("/", normalized);
    }

    private static string BuildUniqueFolderName(Func<string, bool> exists, string baseName)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(baseName) ? "New Folder" : baseName.Trim();
        var candidate = normalizedBase;
        var suffix = 1;
        while (exists(candidate))
        {
            candidate = $"{normalizedBase} ({suffix++})";
        }

        return candidate;
    }

    private async Task<List<RemoteEntry>> ListRemoteFilesRecursivelyAsync(string directoryRelativePath, CancellationToken cancellationToken)
    {
        var results = new List<RemoteEntry>();
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return results;
        }

        var path = BuildSelectedSharePath(directoryRelativePath);
        var children = await _azureFilesBrowserService.ListDirectoryAsync(path, cancellationToken);
        foreach (var child in children)
        {
            if (child.IsDirectory)
            {
                results.AddRange(await ListRemoteFilesRecursivelyAsync(child.FullPath, cancellationToken));
            }
            else
            {
                results.Add(child);
            }
        }

        return results;
    }

    private async Task EnrichLocalEntryAsync(LocalEntry entry)
    {
        if (entry.CreatedTime is not null && !string.IsNullOrWhiteSpace(entry.Author))
        {
            return;
        }

        try
        {
            var details = await _localBrowserService.GetEntryDetailsAsync(entry.FullPath, CancellationToken.None);
            if (details is null)
            {
                return;
            }

            ReplaceLocalEntry(entry, entry with
            {
                CreatedTime = details.CreatedTime,
                Author = details.Author ?? entry.Author
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to enrich local entry metadata for {Path}", entry.FullPath);
        }
    }

    private async Task EnrichRemoteEntryAsync(RemoteEntry entry)
    {
        if ((entry.CreatedTime is not null) && !string.IsNullOrWhiteSpace(entry.Author))
        {
            return;
        }

        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var key = $"{entry.FullPath}|{entry.Name}";
        lock (_remoteEnrichmentLock)
        {
            if (_remoteEnrichmentInFlight.Contains(key))
            {
                return;
            }

            _remoteEnrichmentInFlight.Add(key);
        }

        try
        {
            var wasSelected = string.Equals(SelectedRemoteEntry?.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(SelectedRemoteEntry?.Name, entry.Name, StringComparison.Ordinal);
            var details = await _azureFilesBrowserService.GetEntryDetailsAsync(
                BuildSelectedSharePath(entry.FullPath),
                CancellationToken.None);
            if (details is null)
            {
                return;
            }

            var replacement = entry with
            {
                CreatedTime = details.CreatedTime,
                Author = details.Author ?? entry.Author
            };
            ReplaceRemoteEntry(entry, replacement);

            if (wasSelected)
            {
                _isUpdatingRemoteSelection = true;
                try
                {
                    SelectedRemoteEntry = replacement;
                }
                finally
                {
                    _isUpdatingRemoteSelection = false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to enrich remote entry metadata for {Path}", entry.FullPath);
        }
        finally
        {
            lock (_remoteEnrichmentLock)
            {
                _remoteEnrichmentInFlight.Remove(key);
            }
        }
    }

    private void ReplaceLocalEntry(LocalEntry original, LocalEntry replacement)
    {
        var index = LocalEntries.IndexOf(original);
        if (index >= 0)
        {
            LocalEntries[index] = replacement;
        }

        var gridIndex = LocalGridEntries.IndexOf(original);
        if (gridIndex >= 0)
        {
            LocalGridEntries[gridIndex] = replacement;
        }

        if (ReferenceEquals(SelectedLocalEntry, original))
        {
            SelectedLocalEntry = replacement;
        }
    }

    private void ReplaceRemoteEntry(RemoteEntry original, RemoteEntry replacement)
    {
        var index = RemoteEntries.IndexOf(original);
        if (index >= 0)
        {
            RemoteEntries[index] = replacement;
        }

        var gridIndex = RemoteGridEntries.IndexOf(original);
        if (gridIndex >= 0)
        {
            RemoteGridEntries[gridIndex] = replacement;
        }

    }

    private void RefreshLocalGridEntries()
    {
        LocalGridEntries.Clear();
        var parent = Directory.GetParent(LocalPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            LocalGridEntries.Add(new LocalEntry("..", parent, true, 0, DateTimeOffset.MinValue));
        }

        foreach (var entry in LocalEntries)
        {
            LocalGridEntries.Add(entry);
        }
    }

    private void RefreshRemoteGridEntries()
    {
        RemoteGridEntries.Clear();
        OnPropertyChanged(nameof(RemotePathDisplay));
        var normalized = RemotePath.Replace('\\', '/').Trim('/');
        if (!IsRemoteSearchActive && !string.IsNullOrWhiteSpace(normalized))
        {
            var parent = normalized.Contains('/')
                ? normalized[..normalized.LastIndexOf('/')]
                : string.Empty;
            RemoteGridEntries.Add(new RemoteEntry("..", parent, true, 0, null));
        }

        foreach (var entry in RemoteEntries)
        {
            RemoteGridEntries.Add(entry);
        }

        Log.Debug(
            "Remote grid refreshed. SearchActive={SearchActive} Path={Path} RemoteEntries={RemoteEntriesCount} RemoteGridEntries={RemoteGridEntriesCount}",
            IsRemoteSearchActive,
            RemotePath,
            RemoteEntries.Count,
            RemoteGridEntries.Count);
    }

    private async Task<QueueBatchResult> ResolveAndEnqueueBatchAsync(IReadOnlyList<TransferRequest> requests, bool startImmediately)
    {
        var result = new QueueBatchResult { Total = requests.Count };
        if (requests.Count == 0)
        {
            return result;
        }

        TransferConflictPolicy? doForAllPolicy = null;

        foreach (var request in requests)
        {
            var hasConflict = await _transferConflictProbeService
                .HasConflictAsync(request.Direction, request.LocalPath, request.RemotePath, CancellationToken.None);

            if (!hasConflict)
            {
                _transferQueueService.Enqueue(request with { ConflictNote = "No conflict detected at queue time." }, startImmediately);
                result.Queued++;
                continue;
            }

            result.Conflicts++;
            var policy = request.ConflictPolicy;
            if (policy == TransferConflictPolicy.Ask && doForAllPolicy.HasValue)
            {
                policy = doForAllPolicy.Value;
            }

            if (policy == TransferConflictPolicy.Ask)
            {
                if (!_conflictResolutionPromptService.TryResolveConflict(
                        request.Direction,
                        request.LocalPath,
                        BuildDestinationDisplay(request.RemotePath, request.LocalPath, request.Direction),
                        out var action,
                        out var doForAll))
                {
                    result.BatchCanceled = true;
                    break;
                }

                if (action == ConflictPromptAction.CancelBatch)
                {
                    result.BatchCanceled = true;
                    break;
                }

                policy = action switch
                {
                    ConflictPromptAction.Overwrite => TransferConflictPolicy.Overwrite,
                    ConflictPromptAction.Rename => TransferConflictPolicy.Rename,
                    ConflictPromptAction.Skip => TransferConflictPolicy.Skip,
                    _ => TransferConflictPolicy.Skip
                };

                if (doForAll)
                {
                    doForAllPolicy = policy;
                }
            }

            if (policy == TransferConflictPolicy.Skip)
            {
                result.Skipped++;
                continue;
            }

            var effectiveRequest = request with
            {
                ConflictPolicy = policy,
                ConflictNote = $"Resolved conflict using policy: {policy}"
            };

            if (policy == TransferConflictPolicy.Rename)
            {
                var renamed = await _transferConflictProbeService
                    .ResolveRenameTargetAsync(request.Direction, request.LocalPath, request.RemotePath, CancellationToken.None);
                effectiveRequest = effectiveRequest with
                {
                    LocalPath = renamed.LocalPath,
                    RemotePath = renamed.RemotePath,
                    ConflictNote = "Resolved conflict using policy: Rename (auto-suffix)."
                };
            }

            _transferQueueService.Enqueue(effectiveRequest, startImmediately);
            result.Queued++;
        }

        if (result.BatchCanceled)
        {
            result.Skipped += requests.Count - result.Queued - result.Skipped;
        }

        return result;
    }

    private static string BuildDestinationDisplay(SharePath remotePath, string localPath, TransferDirection direction) =>
        direction == TransferDirection.Upload
            ? $"{remotePath.StorageAccountName}/{remotePath.ShareName}/{remotePath.NormalizeRelativePath()}"
            : localPath;

    private TransferRequest CreateTransferRequest(
        TransferDirection direction,
        string localPath,
        SharePath remotePath,
        TransferConflictPolicy conflictPolicy) =>
        new(
            direction,
            localPath,
            remotePath,
            ConflictPolicy: conflictPolicy,
            IsDirectory: false,
            MaxConcurrency: TransferMaxConcurrency,
            MaxBytesPerSecond: TransferMaxBytesPerSecond);

    private static int NormalizeTransferConcurrency(int value) =>
        Math.Clamp(value <= 0 ? DefaultTransferConcurrency : value, MinTransferConcurrency, MaxTransferConcurrency);

    private static int NormalizeTransferMaxBytesPerSecond(int value) =>
        Math.Clamp(value, MinTransferMaxBytesPerSecond, MaxTransferMaxBytesPerSecond);

    private static TransferConflictPolicy NormalizeConflictPolicy(TransferConflictPolicy value) =>
        Enum.IsDefined(typeof(TransferConflictPolicy), value) ? value : TransferConflictPolicy.Ask;

    private void SetQueueBatchStatus(QueueBatchResult result)
    {
        QueueBatchStatusMessage =
            $"Queue batch: {result.Queued} queued, {result.Skipped} skipped, {result.Conflicts} conflicts" +
            (result.BatchCanceled ? ", batch canceled" : string.Empty);
        OnPropertyChanged(nameof(StatusQueueText));
    }

    private void SetDeleteBatchStatus(string scope, DeleteBatchResult result)
    {
        if (result.Total == 0)
        {
            QueueBatchStatusMessage = $"No {scope} items selected for delete.";
            OnPropertyChanged(nameof(StatusQueueText));
            return;
        }

        QueueBatchStatusMessage = result.Failed == 0
            ? $"Deleted {result.Deleted} {scope} item(s)."
            : $"Deleted {result.Deleted} of {result.Total} {scope} item(s). Failed: {result.Failed}.";
        OnPropertyChanged(nameof(StatusQueueText));
    }


    private sealed class NoOpRemoteEditSessionService : IRemoteEditSessionService
    {
        public static NoOpRemoteEditSessionService Instance { get; } = new();

        public Task<RemoteEditOpenResult> OpenAsync(SharePath remotePath, string displayName, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Remote edit session service is not configured.");
        }

        public Task<IReadOnlyList<RemoteEditPendingChange>> GetPendingChangesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteEditPendingChange>>([]);

        public Task<RemoteEditSyncResult> SyncAsync(Guid sessionId, bool overwriteIfRemoteChanged, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteEditSyncResult(sessionId, RemoteEditSyncOutcome.SessionNotFound));

        public Task<bool> DiscardAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
    private sealed class SchedulerBackedRemoteOperationCoordinator : IRemoteOperationCoordinator
    {
        private readonly IRemoteReadTaskScheduler _scheduler;
        private long _sequence;
        private int _lastCancelReason = (int)RemoteOperationCancelReason.Unknown;

        public SchedulerBackedRemoteOperationCoordinator(IRemoteReadTaskScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public RemoteOperationCancelReason LastCancelReason => (RemoteOperationCancelReason)Volatile.Read(ref _lastCancelReason);

        public Task RunLatestAsync(
            RemoteOperationType operationType,
            Func<RemoteOperationScope, CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            return RunLatestAsync(
                operationType,
                async (scope, token) =>
                {
                    await operation(scope, token);
                    return true;
                },
                cancellationToken);
        }

        public async Task<TResult> RunLatestAsync<TResult>(
            RemoteOperationType operationType,
            Func<RemoteOperationScope, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            var scope = CreateScope(operationType);
            Volatile.Write(ref _lastCancelReason, (int)RemoteOperationCancelReason.Unknown);

            try
            {
                return await _scheduler.RunLatestAsync(token => operation(scope, token), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested && LastCancelReason == RemoteOperationCancelReason.Unknown)
                {
                    Volatile.Write(ref _lastCancelReason, (int)RemoteOperationCancelReason.ReplacedByLatest);
                }

                throw;
            }
        }

        public void CancelCurrent(RemoteOperationCancelReason reason = RemoteOperationCancelReason.UserRequested)
        {
            Volatile.Write(ref _lastCancelReason, (int)reason);
            _scheduler.CancelCurrent();
        }

        private RemoteOperationScope CreateScope(RemoteOperationType operationType)
        {
            return new RemoteOperationScope(
                operationType,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Interlocked.Increment(ref _sequence),
                IsUserInitiated: true);
        }
    }

    private sealed record RemoteViewSnapshot(
        string Path,
        List<RemoteEntry> Entries,
        RemoteEntry? SelectedEntry,
        List<RemoteEntry> SelectedEntries,
        string? ContinuationToken,
        bool HasMore);

    private sealed class QueueBatchResult
    {
        public int Total { get; set; }
        public int Queued { get; set; }
        public int Skipped { get; set; }
        public int Conflicts { get; set; }
        public bool BatchCanceled { get; set; }
    }

    public sealed record DeleteBatchResult(int Total, int Deleted, int Failed);
}











