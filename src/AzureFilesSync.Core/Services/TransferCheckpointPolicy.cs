using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public static class TransferCheckpointPolicy
{
    public static bool IsUsable(
        TransferCheckpoint? checkpoint,
        TransferRequest request,
        long totalBytes,
        string sourceVersion,
        string? stagingPath,
        bool stagingFileExists)
    {
        if (checkpoint is null ||
            checkpoint.Direction != request.Direction ||
            !string.Equals(checkpoint.LocalPath, request.LocalPath, StringComparison.OrdinalIgnoreCase) ||
            checkpoint.TotalBytes != totalBytes ||
            !string.Equals(checkpoint.SourceVersion, sourceVersion, StringComparison.Ordinal) ||
            !RemotePathsMatch(checkpoint.RemotePath, request.RemotePath))
        {
            return false;
        }

        return stagingPath is null ||
               (stagingFileExists &&
                string.Equals(checkpoint.StagingPath, stagingPath, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<TransferRangeCheckpoint> GetUsableCompletedRanges(
        TransferCheckpoint? checkpoint,
        long totalBytes,
        int chunkSize)
    {
        if (checkpoint?.CompletedRanges is not { Count: > 0 } || totalBytes < 0 || chunkSize <= 0)
        {
            return [];
        }

        var expected = new HashSet<TransferRangeCheckpoint>();
        for (long offset = 0; offset < totalBytes; offset += chunkSize)
        {
            expected.Add(new TransferRangeCheckpoint(offset, (int)Math.Min(chunkSize, totalBytes - offset)));
        }

        return checkpoint.CompletedRanges
            .Where(expected.Contains)
            .Distinct()
            .OrderBy(range => range.Offset)
            .ToList();
    }

    private static bool RemotePathsMatch(SharePath left, SharePath right) =>
        left.ProviderKind == right.ProviderKind &&
        string.Equals(left.StorageAccountName, right.StorageAccountName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ShareName, right.ShareName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NormalizeRelativePath(), right.NormalizeRelativePath(), StringComparison.OrdinalIgnoreCase);
}
