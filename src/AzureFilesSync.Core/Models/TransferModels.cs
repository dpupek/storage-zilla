namespace AzureFilesSync.Core.Models;

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferJobStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled
}

public sealed record TransferRequest(
    TransferDirection Direction,
    string LocalPath,
    SharePath RemotePath,
    bool IsDirectory = false,
    int MaxConcurrency = 4,
    int ChunkSizeBytes = 4 * 1024 * 1024);

public sealed record TransferProgress(long BytesTransferred, long TotalBytes);

public sealed record TransferCheckpoint(
    Guid JobId,
    TransferDirection Direction,
    string LocalPath,
    SharePath RemotePath,
    long TotalBytes,
    long NextOffset,
    DateTimeOffset LastUpdatedUtc);

public sealed record TransferJobSnapshot(
    Guid JobId,
    TransferRequest Request,
    TransferJobStatus Status,
    long BytesTransferred,
    long TotalBytes,
    string? Message,
    int RetryCount);
