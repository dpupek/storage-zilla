using System.ComponentModel;

namespace AzureFilesSync.Core.Models;

public sealed record GridColumnProfile(
    string Key,
    bool Visible,
    double Width,
    int DisplayIndex);

public sealed record GridLayoutProfile(
    IReadOnlyList<GridColumnProfile> Columns,
    string? SortColumn,
    ListSortDirection? SortDirection);

public sealed record ConnectionProfile(
    string? SubscriptionId,
    string? StorageAccountName,
    string? FileShareName,
    string LocalPath,
    string RemotePath,
    bool IncludeDeletes,
    int TransferMaxConcurrency,
    int TransferMaxBytesPerSecond,
    TransferConflictPolicy UploadConflictDefaultPolicy,
    TransferConflictPolicy DownloadConflictDefaultPolicy,
    IReadOnlyList<string> RecentLocalPaths,
    IReadOnlyList<string> RecentRemotePaths,
    GridLayoutProfile? LocalGridLayout = null,
    GridLayoutProfile? RemoteGridLayout = null,
    UpdateChannel UpdateChannel = UpdateChannel.Stable)
{
    public static ConnectionProfile Empty(string defaultLocalPath) =>
        new(
            null,
            null,
            null,
            defaultLocalPath,
            string.Empty,
            false,
            4,
            0,
            TransferConflictPolicy.Ask,
            TransferConflictPolicy.Ask,
            [defaultLocalPath],
            [],
            null,
            null,
            UpdateChannel.Stable);
}
