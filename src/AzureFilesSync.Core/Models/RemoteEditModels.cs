namespace AzureFilesSync.Core.Models;

public sealed record RemoteEditOpenResult(Guid SessionId, string LocalPath);

public sealed record RemoteEditPendingChange(
    Guid SessionId,
    string DisplayName,
    string RemotePath,
    string LocalPath,
    bool LocalChanged,
    bool RemoteChanged,
    DateTimeOffset OpenedUtc);

public enum RemoteEditSyncOutcome
{
    Synced,
    LocalFileInUse,
    NoLocalChanges,
    SessionNotFound,
    RemoteChangedNeedsConfirmation
}

public sealed record RemoteEditSyncResult(
    Guid SessionId,
    RemoteEditSyncOutcome Outcome,
    string? Message = null);

