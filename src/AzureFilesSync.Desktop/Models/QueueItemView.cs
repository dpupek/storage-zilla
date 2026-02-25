using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Desktop.Models;

public sealed class QueueItemView
{
    public required TransferJobSnapshot Snapshot { get; init; }
    public Guid JobId => Snapshot.JobId;
    public TransferRequest Request => Snapshot.Request;
    public TransferJobStatus Status => Snapshot.Status;
    public string? Message => Snapshot.Message;
    public string ProgressDisplay => Snapshot.TotalBytes == 0
        ? "0%"
        : $"{(Snapshot.BytesTransferred * 100.0 / Snapshot.TotalBytes):F1}% ({Snapshot.BytesTransferred}/{Snapshot.TotalBytes})";
}
