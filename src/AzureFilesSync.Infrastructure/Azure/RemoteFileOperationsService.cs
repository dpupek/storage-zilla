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

    public async Task RenameAsync(SharePath path, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var serviceClient = BuildServiceClient(path.StorageAccountName);
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

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
        var serviceClient = BuildServiceClient(path.StorageAccountName);
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

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

    private ShareServiceClient BuildServiceClient(string storageAccountName) =>
        new(
            new Uri($"https://{storageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });
}
