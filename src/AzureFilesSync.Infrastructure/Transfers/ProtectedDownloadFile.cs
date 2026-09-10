using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Transfers;

public static class ProtectedDownloadFile
{
    public static async Task SaveCheckpointAsync(
        Stream output, ICheckpointStore store, TransferCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        // Persist metadata only after the bytes it describes leave the stream buffer.
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public static string GetStagingPath(string destinationPath, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The download destination must have a parent directory.");
        var fileName = Path.GetFileName(fullDestinationPath);
        return Path.Combine(directory, $".{fileName}.{jobId:N}.partial");
    }

    public static void Reset(string stagingPath)
    {
        if (File.Exists(stagingPath))
        {
            File.Delete(stagingPath);
        }
    }

    public static void Commit(string stagingPath, string destinationPath)
    {
        if (!File.Exists(stagingPath))
        {
            throw new FileNotFoundException("The completed download staging file was not found.", stagingPath);
        }

        File.Move(stagingPath, destinationPath, overwrite: true);
    }
}
