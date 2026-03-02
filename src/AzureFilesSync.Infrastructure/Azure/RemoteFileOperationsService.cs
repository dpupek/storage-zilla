using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class RemoteFileOperationsService : IRemoteFileOperationsService
{
    private readonly IAuthenticationService _authenticationService;

    public RemoteFileOperationsService(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    public async Task CreateDirectoryAsync(SharePath path, CancellationToken cancellationToken)
    {
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (path.ProviderKind == RemoteProviderKind.AzureBlob)
        {
            var containerClient = BuildBlobServiceClient(path.StorageAccountName).GetBlobContainerClient(path.ShareName);
            var markerClient = containerClient.GetBlobClient(BlobHierarchyPaths.BuildDirectoryMarkerPath(normalized));
            await markerClient.UploadAsync(BinaryData.FromBytes([]), overwrite: true, cancellationToken: cancellationToken);
            return;
        }

        var serviceClient = BuildFileShareServiceClient(path.StorageAccountName);
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        await shareClient.GetDirectoryClient(normalized).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task RenameAsync(SharePath path, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (path.ProviderKind == RemoteProviderKind.AzureBlob)
        {
            await RenameBlobEntryAsync(path, normalized, newName, cancellationToken).ConfigureAwait(false);
            return;
        }

        var serviceClient = BuildFileShareServiceClient(path.StorageAccountName);
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var directoryPath = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : string.Empty;
        var targetPath = string.IsNullOrWhiteSpace(directoryPath) ? newName : $"{directoryPath}/{newName}";

        var parent = string.IsNullOrWhiteSpace(directoryPath)
            ? shareClient.GetRootDirectoryClient()
            : shareClient.GetDirectoryClient(directoryPath);

        var name = normalized.Contains('/') ? normalized[(normalized.LastIndexOf('/') + 1)..] : normalized;
        var subDir = parent.GetSubdirectoryClient(name);
        try
        {
            await subDir.RenameAsync(targetPath, cancellationToken: cancellationToken);
            return;
        }
        catch
        {
            var fileClient = parent.GetFileClient(name);
            await fileClient.RenameAsync(targetPath, cancellationToken: cancellationToken);
        }
    }

    public async Task DeleteAsync(SharePath path, bool recursive, CancellationToken cancellationToken)
    {
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (path.ProviderKind == RemoteProviderKind.AzureBlob)
        {
            await DeleteBlobEntryAsync(path, normalized, recursive, cancellationToken).ConfigureAwait(false);
            return;
        }

        var serviceClient = BuildFileShareServiceClient(path.StorageAccountName);
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var directoryPath = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : string.Empty;
        var name = normalized.Contains('/') ? normalized[(normalized.LastIndexOf('/') + 1)..] : normalized;

        var parent = string.IsNullOrWhiteSpace(directoryPath)
            ? shareClient.GetRootDirectoryClient()
            : shareClient.GetDirectoryClient(directoryPath);

        var subDir = parent.GetSubdirectoryClient(name);
        try
        {
            if (recursive)
            {
                await DeleteDirectoryRecursiveAsync(subDir, cancellationToken);
            }
            await subDir.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return;
        }
        catch
        {
            await parent.GetFileClient(name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
    }

    private async Task RenameBlobEntryAsync(SharePath path, string normalized, string newName, CancellationToken cancellationToken)
    {
        var containerClient = BuildBlobServiceClient(path.StorageAccountName).GetBlobContainerClient(path.ShareName);
        var directoryPath = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : string.Empty;
        var targetPath = string.IsNullOrWhiteSpace(directoryPath) ? newName : $"{directoryPath}/{newName}";

        var sourceBlob = containerClient.GetBlobClient(normalized);
        if (await sourceBlob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await CopyAndDeleteBlobAsync(sourceBlob, containerClient.GetBlobClient(targetPath), cancellationToken).ConfigureAwait(false);
            return;
        }

        var sourcePrefix = BlobHierarchyPaths.NormalizePrefix(normalized);
        var targetPrefix = BlobHierarchyPaths.NormalizePrefix(targetPath);
        await foreach (var blob in containerClient.GetBlobsAsync(prefix: sourcePrefix, cancellationToken: cancellationToken))
        {
            var suffix = blob.Name[sourcePrefix.Length..];
            var destinationName = $"{targetPrefix}{suffix}";
            await CopyAndDeleteBlobAsync(containerClient.GetBlobClient(blob.Name), containerClient.GetBlobClient(destinationName), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteBlobEntryAsync(SharePath path, string normalized, bool recursive, CancellationToken cancellationToken)
    {
        var containerClient = BuildBlobServiceClient(path.StorageAccountName).GetBlobContainerClient(path.ShareName);
        var fileCandidate = containerClient.GetBlobClient(normalized);
        if (await fileCandidate.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await fileCandidate.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!recursive)
        {
            return;
        }

        var prefix = BlobHierarchyPaths.NormalizePrefix(normalized);
        await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            await containerClient.GetBlobClient(blob.Name)
                .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task CopyAndDeleteBlobAsync(BlobClient source, BlobClient destination, CancellationToken cancellationToken)
    {
        await destination.StartCopyFromUriAsync(source.Uri, cancellationToken: cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var props = await destination.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (props.Value.CopyStatus != global::Azure.Storage.Blobs.Models.CopyStatus.Pending)
            {
                if (props.Value.CopyStatus != global::Azure.Storage.Blobs.Models.CopyStatus.Success)
                {
                    throw new InvalidOperationException(
                        $"Failed to copy blob '{source.Name}' to '{destination.Name}'. Status: {props.Value.CopyStatus}");
                }

                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        await source.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteDirectoryRecursiveAsync(ShareDirectoryClient directoryClient, CancellationToken cancellationToken)
    {
        await foreach (var item in directoryClient.GetFilesAndDirectoriesAsync(cancellationToken: cancellationToken))
        {
            if (item.IsDirectory)
            {
                var childDir = directoryClient.GetSubdirectoryClient(item.Name);
                await DeleteDirectoryRecursiveAsync(childDir, cancellationToken);
                await childDir.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
            else
            {
                await directoryClient.GetFileClient(item.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
        }
    }

    private ShareServiceClient BuildFileShareServiceClient(string storageAccountName) =>
        new(
            new Uri($"https://{storageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });

    private BlobServiceClient BuildBlobServiceClient(string storageAccountName) =>
        new(
            new Uri($"https://{storageAccountName}.blob.core.windows.net"),
            _authenticationService.GetCredential());
}
