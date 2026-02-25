namespace AzureFilesSync.Core.Models;

public enum RemoteAccessState
{
    Unknown,
    Accessible,
    PermissionDenied,
    NotFound,
    TransientFailure,
    InvalidSelection
}

public sealed record RemoteContext(
    string StorageAccountName,
    string ShareName,
    string Path,
    string? SubscriptionId = null)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(StorageAccountName) &&
        !string.IsNullOrWhiteSpace(ShareName);
}

public sealed record RemoteCapabilitySnapshot(
    RemoteAccessState State,
    bool CanBrowse,
    bool CanUpload,
    bool CanDownload,
    bool CanPlanMirror,
    bool CanExecuteMirror,
    string UserMessage,
    DateTimeOffset EvaluatedUtc,
    string? ErrorCode = null,
    int? HttpStatus = null)
{
    public static RemoteCapabilitySnapshot InvalidSelection(string message) =>
        new(RemoteAccessState.InvalidSelection, false, false, false, false, false, message, DateTimeOffset.UtcNow);

    public static RemoteCapabilitySnapshot Accessible() =>
        new(RemoteAccessState.Accessible, true, true, true, true, true, string.Empty, DateTimeOffset.UtcNow);
}

public sealed record RemoteActionInputs(
    bool HasSelectedLocalFile,
    bool HasSelectedRemoteFile,
    bool HasMirrorPlan,
    bool IsMirrorPlanning);

public sealed record RemoteActionPolicy(
    bool CanEnqueueUpload,
    bool CanEnqueueDownload,
    bool CanPlanMirror,
    bool CanExecuteMirror,
    string? DisableReason = null);
