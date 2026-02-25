using System.Collections.ObjectModel;
using Azure;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;

namespace AzureFilesSync.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAuthenticationService _authenticationService;
    private readonly IAzureDiscoveryService _azureDiscoveryService;
    private readonly ILocalBrowserService _localBrowserService;
    private readonly IAzureFilesBrowserService _azureFilesBrowserService;
    private readonly ILocalFileOperationsService _localFileOperationsService;
    private readonly IRemoteFileOperationsService _remoteFileOperationsService;
    private readonly ITransferQueueService _transferQueueService;
    private readonly IMirrorPlannerService _mirrorPlanner;
    private readonly IMirrorExecutionService _mirrorExecution;
    private readonly IConnectionProfileStore _connectionProfileStore;
    private readonly IRemoteCapabilityService _remoteCapabilityService;
    private readonly IRemoteActionPolicyService _remoteActionPolicyService;

    private MirrorPlan? _lastMirrorPlan;
    private bool _isRestoringProfile;
    private bool _suppressSelectionHandlers;
    private CancellationTokenSource _selectionCts = new();
    private DateTimeOffset _lastLocalRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRemoteRefreshUtc = DateTimeOffset.MinValue;

    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];
    public ObservableCollection<StorageAccountItem> StorageAccounts { get; } = [];
    public ObservableCollection<FileShareItem> FileShares { get; } = [];
    public ObservableCollection<LocalEntry> LocalEntries { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteEntries { get; } = [];
    public ObservableCollection<LocalEntry> LocalGridEntries { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteGridEntries { get; } = [];
    public ObservableCollection<QueueItemView> QueueItems { get; } = [];
    public ObservableCollection<string> RecentLocalPaths { get; } = [];
    public ObservableCollection<string> RecentRemotePaths { get; } = [];

    [ObservableProperty]
    private string _loginStatus = "Not signed in";

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
    [NotifyCanExecuteChangedFor(nameof(BuildMirrorPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueDownloadCommand))]
    private bool _isMirrorPlanning;

    [ObservableProperty]
    private string _mirrorPlanStatusMessage = string.Empty;

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
        ITransferQueueService transferQueueService,
        IMirrorPlannerService mirrorPlanner,
        IMirrorExecutionService mirrorExecution,
        IConnectionProfileStore connectionProfileStore,
        IRemoteCapabilityService remoteCapabilityService,
        IRemoteActionPolicyService remoteActionPolicyService)
    {
        _authenticationService = authenticationService;
        _azureDiscoveryService = azureDiscoveryService;
        _localBrowserService = localBrowserService;
        _azureFilesBrowserService = azureFilesBrowserService;
        _localFileOperationsService = localFileOperationsService;
        _remoteFileOperationsService = remoteFileOperationsService;
        _transferQueueService = transferQueueService;
        _mirrorPlanner = mirrorPlanner;
        _mirrorExecution = mirrorExecution;
        _connectionProfileStore = connectionProfileStore;
        _remoteCapabilityService = remoteCapabilityService;
        _remoteActionPolicyService = remoteActionPolicyService;

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

            Subscriptions.Clear();
            await foreach (var subscription in _azureDiscoveryService.ListSubscriptionsAsync(CancellationToken.None))
            {
                Subscriptions.Add(subscription);
            }
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
        try
        {
            LocalPath = NormalizeLocalPath(LocalPath);
            Log.Debug("Loading local directory: {LocalPath}", LocalPath);
            LocalEntries.Clear();
            foreach (var item in await _localBrowserService.ListDirectoryAsync(LocalPath, CancellationToken.None))
            {
                LocalEntries.Add(item);
            }
            RefreshLocalGridEntries();
            Log.Debug("Loaded {EntryCount} local entries from {LocalPath}", LocalEntries.Count, LocalPath);

            AddRecentPath(RecentLocalPaths, LocalPath);
            await PersistProfileAsync();
        }
        catch (Exception ex)
        {
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
        var request = new TransferRequest(TransferDirection.Upload, SelectedLocalEntry.FullPath, new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, remoteRelative));
        _transferQueueService.Enqueue(request, startImmediately: true);
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
        var request = new TransferRequest(TransferDirection.Download, localTarget, new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, SelectedRemoteEntry.FullPath));
        _transferQueueService.Enqueue(request, startImmediately: true);
        await PersistProfileAsync();
    }

    [RelayCommand]
    private async Task PauseSelectedJobAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        await _transferQueueService.PauseAsync(SelectedQueueItem.JobId, CancellationToken.None);
    }

    [RelayCommand]
    private async Task ResumeSelectedJobAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        await _transferQueueService.ResumeAsync(SelectedQueueItem.JobId, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RetrySelectedJobAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        await _transferQueueService.RetryAsync(SelectedQueueItem.JobId, CancellationToken.None);
    }

    [RelayCommand]
    private async Task CancelSelectedJobAsync()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        await _transferQueueService.CancelAsync(SelectedQueueItem.JobId, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RunQueueAsync()
    {
        await _transferQueueService.RunQueuedAsync(CancellationToken.None);
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
        MessageBox.Show(
            "Settings UI is not implemented yet.\n\nFor now, use the main window controls and Save Profile.",
            "Settings",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    [RelayCommand]
    private void OpenHelp()
    {
        MessageBox.Show(
            "Quick Start:\n1. Sign in\n2. Choose subscription, storage account, and share\n3. Browse local and remote paths\n4. Queue uploads/downloads or use mirror planning",
            "Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        MessageBox.Show(
            $"Storage Zilla\nVersion: {version}",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

        if (value is null || _isRestoringProfile)
        {
            return;
        }

        _ = PersistProfileAsync();
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
        if (value is null || value.Name == ".." || SelectedStorageAccount is null || SelectedFileShare is null)
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
        StorageAccounts.Clear();
        await foreach (var account in _azureDiscoveryService.ListStorageAccountsAsync(subscription.Id, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(account.Name))
            {
                StorageAccounts.Add(account);
            }
        }

        if (SelectedStorageAccount is null || StorageAccounts.All(x => x.Name != SelectedStorageAccount.Name))
        {
            SetSelectionSilently(() => SelectedStorageAccount = StorageAccounts.FirstOrDefault());
        }

        Log.Debug("Loaded {StorageAccountCount} storage accounts.", StorageAccounts.Count);
    }

    private async Task LoadFileSharesAsync(StorageAccountItem account, CancellationToken cancellationToken)
    {
        Log.Debug("Loading file shares for storage account {StorageAccountName}", account.Name);
        FileShares.Clear();
        try
        {
            await foreach (var share in _azureDiscoveryService.ListFileSharesAsync(account.Name, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(share.Name))
                {
                    FileShares.Add(share);
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
            RecentLocalPaths.ToList(),
            RecentRemotePaths.ToList(),
            LocalGridLayout,
            RemoteGridLayout);

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

        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
            {
                var remotePrefix = CombineRemotePath(RemotePath, entry.Name);
                foreach (var filePath in Directory.EnumerateFiles(entry.FullPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(entry.FullPath, filePath).Replace('\\', '/');
                    var remoteRelative = CombineRemotePath(remotePrefix, relative);
                    var request = new TransferRequest(
                        TransferDirection.Upload,
                        filePath,
                        new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, remoteRelative));
                    _transferQueueService.Enqueue(request, startImmediately);
                }
            }
            else
            {
                var remoteRelative = CombineRemotePath(RemotePath, entry.Name);
                var request = new TransferRequest(
                    TransferDirection.Upload,
                    entry.FullPath,
                    new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, remoteRelative));
                _transferQueueService.Enqueue(request, startImmediately);
            }
        }

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

        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
            {
                var files = await ListRemoteFilesRecursivelyAsync(entry.FullPath, CancellationToken.None);
                foreach (var file in files)
                {
                    var relativeUnderFolder = file.FullPath[entry.FullPath.Length..].TrimStart('/');
                    var localTarget = Path.Combine(LocalPath, entry.Name, relativeUnderFolder.Replace('/', Path.DirectorySeparatorChar));
                    var request = new TransferRequest(
                        TransferDirection.Download,
                        localTarget,
                        new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, file.FullPath));
                    _transferQueueService.Enqueue(request, startImmediately);
                }
            }
            else
            {
                var localTarget = Path.Combine(LocalPath, entry.Name);
                var request = new TransferRequest(
                    TransferDirection.Download,
                    localTarget,
                    new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, entry.FullPath));
                _transferQueueService.Enqueue(request, startImmediately);
            }
        }

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

    private bool CanBuildMirrorPlan() => BuildRemotePolicy().CanPlanMirror;
    private bool CanExecuteMirror() => BuildRemotePolicy().CanExecuteMirror;
    private bool CanEnqueueUpload() => BuildRemotePolicy().CanEnqueueUpload;
    private bool CanEnqueueDownload() => BuildRemotePolicy().CanEnqueueDownload;

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

        try
        {
            var details = await _azureFilesBrowserService.GetEntryDetailsAsync(
                new SharePath(SelectedStorageAccount.Name, SelectedFileShare.Name, entry.FullPath),
                CancellationToken.None);
            if (details is null)
            {
                return;
            }

            ReplaceRemoteEntry(entry, entry with
            {
                CreatedTime = details.CreatedTime,
                Author = details.Author ?? entry.Author
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to enrich remote entry metadata for {Path}", entry.FullPath);
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

        if (ReferenceEquals(SelectedRemoteEntry, original))
        {
            SelectedRemoteEntry = replacement;
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
}
