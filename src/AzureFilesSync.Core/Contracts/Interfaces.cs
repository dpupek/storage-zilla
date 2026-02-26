using Azure.Core;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Contracts;

public interface IAuthenticationService
{
    Task<LoginSession> SignInInteractiveAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
    TokenCredential GetCredential();
}

public interface IAzureDiscoveryService
{
    IAsyncEnumerable<SubscriptionItem> ListSubscriptionsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<StorageAccountItem> ListStorageAccountsAsync(string subscriptionId, CancellationToken cancellationToken);
    IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, CancellationToken cancellationToken);
}

public interface ILocalBrowserService
{
    Task<IReadOnlyList<LocalEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<LocalEntry?> GetEntryDetailsAsync(string fullPath, CancellationToken cancellationToken);
}

public interface IAzureFilesBrowserService
{
    Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken);
    Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken);
}

public interface ILocalFileOperationsService
{
    Task ShowInExplorerAsync(string path, CancellationToken cancellationToken);
    Task OpenAsync(string path, CancellationToken cancellationToken);
    Task OpenWithAsync(string path, CancellationToken cancellationToken);
    Task RenameAsync(string path, string newName, CancellationToken cancellationToken);
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);
}

public interface IRemoteFileOperationsService
{
    Task RenameAsync(SharePath path, string newName, CancellationToken cancellationToken);
    Task DeleteAsync(SharePath path, bool recursive, CancellationToken cancellationToken);
}

public interface ITransferExecutor
{
    Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken);
    Task ExecuteAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken);
}

public interface ITransferConflictProbeService
{
    Task<bool> HasConflictAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken);
    Task<(string LocalPath, SharePath RemotePath)> ResolveRenameTargetAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken);
}

public interface ICheckpointStore
{
    Task<TransferCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken);
    Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface ITransferQueueService
{
    event EventHandler<TransferJobSnapshot>? JobUpdated;
    EnqueueResult EnqueueOrGetExisting(TransferRequest request, bool startImmediately = true);
    Guid Enqueue(TransferRequest request, bool startImmediately = true);
    Task PauseAsync(Guid jobId, CancellationToken cancellationToken);
    Task ResumeAsync(Guid jobId, CancellationToken cancellationToken);
    Task RunQueuedAsync(CancellationToken cancellationToken);
    Task PauseAllAsync(CancellationToken cancellationToken);
    Task RetryAsync(Guid jobId, CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken);
    IReadOnlyList<TransferJobSnapshot> Snapshot();
}

public interface IConnectionProfileStore
{
    Task<ConnectionProfile> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}

public interface IMirrorPlannerService
{
    Task<MirrorPlan> BuildPlanAsync(MirrorSpec spec, CancellationToken cancellationToken);
}

public interface IMirrorExecutionService
{
    Task<MirrorExecutionResult> ExecuteAsync(MirrorPlan plan, CancellationToken cancellationToken);
}

public interface IRemoteErrorInterpreter
{
    RemoteCapabilitySnapshot Interpret(Exception exception, RemoteContext context);
}

public interface IRemoteCapabilityService
{
    Task<RemoteCapabilitySnapshot> EvaluateAsync(RemoteContext context, CancellationToken cancellationToken);
    Task<RemoteCapabilitySnapshot> RefreshAsync(RemoteContext context, CancellationToken cancellationToken);
    RemoteCapabilitySnapshot? GetLastKnown(RemoteContext context);
}

public interface IRemoteActionPolicyService
{
    RemoteActionPolicy Compute(RemoteCapabilitySnapshot? capability, RemoteActionInputs inputs);
}
