using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Desktop.Models;

public sealed class QueueItemView
{
    public required TransferJobSnapshot Snapshot { get; init; }
    public Guid JobId => Snapshot.JobId;
    public TransferRequest Request => Snapshot.Request;
    public string LocalPathDisplay => Snapshot.Request.LocalPath;
    public string RemotePathDisplay =>
        $"{Snapshot.Request.RemotePath.StorageAccountName}/{Snapshot.Request.RemotePath.ShareName}/{Snapshot.Request.RemotePath.NormalizeRelativePath()}";
    public TransferConflictPolicy ConflictPolicy => Snapshot.Request.ConflictPolicy;
    public string ConflictPolicyDisplay =>
        string.IsNullOrWhiteSpace(Snapshot.Request.ConflictNote)
            ? Snapshot.Request.ConflictPolicy.ToString()
            : $"{Snapshot.Request.ConflictPolicy}: {Snapshot.Request.ConflictNote}";
    public TransferJobStatus Status => Snapshot.Status;
    public string? Message => Snapshot.Message;
    public string ProgressDisplay
    {
        get
        {
            if (Snapshot.TotalBytes == 0)
            {
                return Snapshot.Status == TransferJobStatus.Completed ? "100% (0/0)" : "0%";
            }

            return $"{(Snapshot.BytesTransferred * 100.0 / Snapshot.TotalBytes):F1}% ({Snapshot.BytesTransferred}/{Snapshot.TotalBytes})";
        }
    }
}
