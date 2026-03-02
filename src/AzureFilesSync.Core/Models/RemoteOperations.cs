namespace AzureFilesSync.Core.Models;

public enum RemoteOperationType
{
    Browse,
    LoadMore,
    Search,
    SelectionChange,
    ProfileRestore
}

public enum RemoteOperationCancelReason
{
    Unknown,
    UserRequested,
    ReplacedByLatest,
    SelectionChanged,
    SignOut
}

public readonly record struct RemoteOperationScope(
    RemoteOperationType OperationType,
    Guid CorrelationId,
    DateTimeOffset StartedUtc,
    long Sequence,
    bool IsUserInitiated);
