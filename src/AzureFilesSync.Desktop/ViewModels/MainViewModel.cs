using System.Collections.ObjectModel;
using Azure;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.Models;
using AzureFilesSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;

namespace AzureFilesSync.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int DefaultTransferConcurrency = 4;
    private const int MinTransferConcurrency = 1;
    private const int MaxTransferConcurrency = 32;
    private const int MinTransferMaxBytesPerSecond = 0;
    private const int MaxTransferMaxBytesPerSecond = 1024 * 1024 * 1024;
    private const string QueueFilterAll = "All";

    private readonly IAuthenticationService _authenticationService;
    private readonly IAzureDiscoveryService _azureDiscoveryService;
    private readonly ILocalBrowserService _localBrowserService;
    private readonly IAzureFilesBrowserService _azureFilesBrowserService;
    private readonly ILocalFileOperationsService _localFileOperationsService;
    private readonly IRemoteFileOperationsService _remoteFileOperationsService;
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

    private MirrorPlan? _lastMirrorPlan;
    private bool _isRestoringProfile;
    private bool _suppressSelectionHandlers;
    private bool _isUpdatingRemoteSelection;
    private CancellationTokenSource _selectionCts = new();
    private DateTimeOffset _lastLocalRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRemoteRefreshUtc = DateTimeOffset.MinValue;
    private string _lastSuccessfulLocalPath = NormalizeLocalPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private readonly Lock _remoteEnrichmentLock = new();
    private readonly HashSet<string> _remoteEnrichmentInFlight = [];

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

    public string RemotePathDisplay
    {
        get => string.IsNullOrWhiteSpace(RemotePath) ? @"\" : RemotePath;
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
    private RemoteCapabilitySnapshot? _remoteCapability;

    [ObservableProperty]
    private SubscriptionItem? _selectedSubscription;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    private StorageAccountItem? _selectedStorageAccount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
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
        ILocalBrowserService localBrowserService,
        IAzureFilesBrowserService azureFilesBrowserService,
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
        IUserHelpContentService userHelpContentService)
    {
        _authenticationService = authenticationService;
        _azureDiscoveryService = azureDiscoveryService;
        _localBrowserService = localBrowserService;
        _azureFilesBrowserService = azureFilesBrowserService;
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
        UpdateChannel = _appUpdateService.CurrentChannel;

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
            LoginStatus = $"Signed in as {session.DisplayName}";
            Log.Information("Sign-in completed for {DisplayName}", session.DisplayName);

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
        try
        {
            var context = BuildRemoteContext();
            var capability = await _remoteCapabilityService.RefreshAsync(context, CancellationToken.None);
            ApplyCapability(capability);

            if (!capability.CanBrowse)
            {
                RemoteEntries.Clear();
                RemoteGridEntries.Clear();
                return;
            }

            RemoteEntries.Clear();
            var path = new SharePath(context.StorageAccountName, context.ShareName, context.Path);
            foreach (var item in await _azureFilesBrowserService.ListDirectoryAsync(path, CancellationToken.None))
            {
                RemoteEntries.Add(item);
            }
            RefreshRemoteGridEntries();

            AddRecentPath(RecentRemotePaths, RemotePath);
            await PersistProfileAsync();
            Log.Debug(
                "Loaded {EntryCount} remote entries. Account={Account} Share={Share} Path={RemotePath}",
                RemoteEntries.Count,
                context.StorageAccountName,
                context.ShareName,
                context.Path);
        }
        catch (RequestFailedException ex)
        {
            var context = BuildRemoteContext();
            var capability = await _remoteCapabilityService.RefreshAsync(context, CancellationToken.None);
            ApplyCapability(capability);
            RemoteEntries.Clear();
            RemoteGridEntries.Clear();

            if (capability.State is RemoteAccessState.PermissionDenied or RemoteAccessState.NotFound or RemoteAccessState.TransientFailure)
            {
                Log.Warning(ex, "Remote directory load mapped to capability state {State}", capability.State);
                return;
            }

            ShowError("Failed to load remote directory.", ex);
        }
        catch (Exception ex)
        {
            RemoteEntries.Clear();
            RemoteGridEntries.Clear();
            ShowError("Failed to load remote directory.", ex);
        }
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
            new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, remoteRelative),
            UploadConflictDefaultPolicy);
        var batch = await ResolveAndEnqueueBatchAsync([request], startImmediately: true);
        SetQueueBatchStatus(batch);
        await PersistProfileAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEnqueueDownload))]
    private async Task EnqueueDownloadAsync()
    {
        if (SelectedRemoteEntry is null || SelectedRemoteEntry.Name == ".." || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        var normalizedLocalPath = NormalizeLocalPath(LocalPath);
        LocalPath = normalizedLocalPath;
        var localTarget = Path.Combine(normalizedLocalPath, SelectedRemoteEntry.Name);
        var request = CreateTransferRequest(
            TransferDirection.Download,
            localTarget,
            new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, SelectedRemoteEntry.FullPath),
            DownloadConflictDefaultPolicy);
        var batch = await ResolveAndEnqueueBatchAsync([request], startImmediately: true);
        SetQueueBatchStatus(batch);
        await PersistProfileAsync();
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

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            await _authenticationService.SignOutAsync(CancellationToken.None);
            LoginStatus = "Not signed in";
            Subscriptions.Clear();
            StorageAccounts.Clear();
            FileShares.Clear();
            RemoteEntries.Clear();
            RemoteGridEntries.Clear();
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Sign in to browse Azure file shares."));
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
            new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, RemotePath),
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

        if (value is null || _isRestoringProfile || _suppressSelectionHandlers)
        {
            return;
        }

        StartSelectionChangeLoad(
            token => HandleStorageAccountSelectionChangedAsync(value, token),
            "Failed to load file shares for selected storage account.");
    }

    partial void OnSelectedFileShareChanged(FileShareItem? value)
    {
        _lastMirrorPlan = null;
        ExecuteMirrorCommand.NotifyCanExecuteChanged();

        if (value is null || _isRestoringProfile || _suppressSelectionHandlers)
        {
            return;
        }

        StartSelectionChangeLoad(
            async token =>
            {
                token.ThrowIfCancellationRequested();
                await LoadRemoteDirectoryAsync();
                token.ThrowIfCancellationRequested();
                await PersistProfileAsync();
            },
            "Failed to load remote directory for selected file share.");
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
            RemoteEntries.Clear();
            RemoteGridEntries.Clear();
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Select a storage account and file share."));
        }

        await PersistProfileAsync();
    }

    private async Task HandleStorageAccountSelectionChangedAsync(StorageAccountItem value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value.Name))
        {
            FileShares.Clear();
            RemoteEntries.Clear();
            RemoteGridEntries.Clear();
            ApplyCapability(RemoteCapabilitySnapshot.InvalidSelection("Selected storage account is missing a valid name."));
            LoginStatus = "Signed in. Selected storage account is missing a valid name.";
            return;
        }

        Log.Information("Storage account changed to {StorageAccount}. Resetting remote path.", value.Name);
        RemotePath = string.Empty;
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
        Log.Debug("Loading file shares for storage account {StorageAccountName}", account.Name);
        var fileShares = new List<FileShareItem>();
        try
        {
            await foreach (var share in _azureDiscoveryService.ListFileSharesAsync(account.Name, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(share.Name))
                {
                    fileShares.Add(share);
                }
            }
        }
        catch (RequestFailedException ex)
        {
            RemoteEntries.Clear();
            RemoteGridEntries.Clear();
            var capability = _remoteCapabilityService.GetLastKnown(BuildRemoteContext())
                ?? new RemoteCapabilitySnapshot(
                    RemoteAccessState.Unknown,
                    false,
                    false,
                    false,
                    false,
                    false,
                    $"Cannot list shares for '{account.Name}' (HTTP {ex.Status}).",
                    DateTimeOffset.UtcNow,
                    ex.ErrorCode,
                    ex.Status);
            ApplyCapability(capability with { UserMessage = $"Cannot list shares for '{account.Name}' (HTTP {ex.Status}). Verify Azure Files data access." });
            LoginStatus = $"Signed in. Cannot list shares for '{account.Name}' ({ex.Status}).";
            return;
        }

        ReplaceSortedCollection(FileShares, fileShares, x => x.Name);

        if (SelectedFileShare is null || FileShares.All(x => x.Name != SelectedFileShare.Name))
        {
            SetSelectionSilently(() => SelectedFileShare = FileShares.FirstOrDefault());
        }

        Log.Debug("Loaded {ShareCount} file shares for {StorageAccountName}", FileShares.Count, account.Name);

        cancellationToken.ThrowIfCancellationRequested();
        await LoadRemoteDirectoryAsync();
    }

    private async Task LoadLocalProfileDefaultsAsync()
    {
        var profile = await _connectionProfileStore.LoadAsync(CancellationToken.None);
        _isRestoringProfile = true;
        try
        {
            LocalPath = NormalizeLocalPath(profile.LocalPath);
            RemotePath = profile.RemotePath;
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

            var share = FileShares.FirstOrDefault(x => string.Equals(x.Name, profile.FileShareName, StringComparison.OrdinalIgnoreCase));
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
            UpdateChannel);

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
                        new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, remoteRelative),
                        UploadConflictDefaultPolicy));
                }
            }
            else
            {
                var remoteRelative = CombineRemotePath(RemotePath, entry.Name);
                requests.Add(CreateTransferRequest(
                    TransferDirection.Upload,
                    entry.FullPath,
                    new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, remoteRelative),
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

        var requests = new List<TransferRequest>();
        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
            {
                var files = await ListRemoteFilesRecursivelyAsync(entry.FullPath, CancellationToken.None);
                foreach (var file in files)
                {
                    var relativeUnderFolder = file.FullPath[entry.FullPath.Length..].TrimStart('/');
                    var localTarget = Path.Combine(LocalPath, entry.Name, relativeUnderFolder.Replace('/', Path.DirectorySeparatorChar));
                    requests.Add(CreateTransferRequest(
                        TransferDirection.Download,
                        localTarget,
                        new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, file.FullPath),
                        DownloadConflictDefaultPolicy));
                }
            }
            else
            {
                var localTarget = Path.Combine(LocalPath, entry.Name);
                requests.Add(CreateTransferRequest(
                    TransferDirection.Download,
                    localTarget,
                    new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, entry.FullPath),
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

    public async Task RenameRemoteAsync(RemoteEntry? entry, string newName)
    {
        if (entry is null || entry.Name == ".." || string.IsNullOrWhiteSpace(newName) || SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return;
        }

        await _remoteFileOperationsService.RenameAsync(
            new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, entry.FullPath),
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
            new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, entry.FullPath),
            recursive,
            CancellationToken.None);
        await LoadRemoteDirectoryAsync();
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
        if (entry is null || !entry.IsDirectory)
        {
            return;
        }

        RemotePath = entry.FullPath;
        await LoadRemoteDirectoryAsync();
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
        });
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
        EnqueueUploadCommand.NotifyCanExecuteChanged();
        EnqueueDownloadCommand.NotifyCanExecuteChanged();
        BuildMirrorPlanCommand.NotifyCanExecuteChanged();
        ExecuteMirrorCommand.NotifyCanExecuteChanged();
    }

    private RemoteContext BuildRemoteContext() =>
        new(
            SelectedStorageAccount?.Name ?? string.Empty,
            SelectedFileShare?.Name ?? string.Empty,
            RemotePath,
            SelectedSubscription?.Id);

    private RemoteActionPolicy BuildRemotePolicy()
    {
        var inputs = new RemoteActionInputs(
            HasSelectedLocalFile: SelectedLocalEntry is { IsDirectory: false },
            HasSelectedRemoteFile: SelectedRemoteEntry is { IsDirectory: false },
            HasMirrorPlan: _lastMirrorPlan is not null,
            IsMirrorPlanning: IsMirrorPlanning);
        return _remoteActionPolicyService.Compute(RemoteCapability, inputs);
    }

    private static void ShowError(string summary, Exception ex)
    {
        ErrorDialog.Show(summary, ex);
    }

    private static string NormalizeLocalPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path.Trim();
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string NormalizeRemotePathDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed is @"\" or "/")
        {
            return string.Empty;
        }

        return trimmed.Replace('\\', '/').Trim('/');
    }

    private bool CanBuildMirrorPlan() => BuildRemotePolicy().CanPlanMirror;
    private bool CanExecuteMirror() => BuildRemotePolicy().CanExecuteMirror;
    private bool CanEnqueueUpload() => BuildRemotePolicy().CanEnqueueUpload;
    private bool CanEnqueueDownload() => BuildRemotePolicy().CanEnqueueDownload;
    private bool CanActOnSelectedQueueItems() => SelectedQueueCount > 0;

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

    partial void OnRemotePathChanged(string value)
    {
        OnPropertyChanged(nameof(RemotePathDisplay));
    }

    partial void OnLoginStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusThrottleText));
        OnPropertyChanged(nameof(StatusConcurrencyText));
        OnPropertyChanged(nameof(StatusQueueText));
    }

    partial void OnSelectedQueueCountChanged(int value)
    {
        PauseSelectedJobCommand.NotifyCanExecuteChanged();
        ResumeSelectedJobCommand.NotifyCanExecuteChanged();
        RetrySelectedJobCommand.NotifyCanExecuteChanged();
        CancelSelectedJobCommand.NotifyCanExecuteChanged();
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
        _selectionCts.Cancel();
        _selectionCts.Dispose();
        _selectionCts = new CancellationTokenSource();
        var token = _selectionCts.Token;

        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await operation(token);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("Selection change operation canceled.");
            }
            catch (Exception ex)
            {
                ShowError(errorMessage, ex);
            }
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

    private async Task<List<RemoteEntry>> ListRemoteFilesRecursivelyAsync(string directoryRelativePath, CancellationToken cancellationToken)
    {
        var results = new List<RemoteEntry>();
        if (SelectedStorageAccount is null || SelectedFileShare is null)
        {
            return results;
        }

        var path = new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, directoryRelativePath);
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
                new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, entry.FullPath),
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
        var normalized = RemotePath.Replace('\\', '/').Trim('/');
        if (!string.IsNullOrWhiteSpace(normalized))
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

    private sealed class QueueBatchResult
    {
        public int Total { get; set; }
        public int Queued { get; set; }
        public int Skipped { get; set; }
        public int Conflicts { get; set; }
        public bool BatchCanceled { get; set; }
    }
}
