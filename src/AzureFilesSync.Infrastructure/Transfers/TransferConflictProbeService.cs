using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Transfers;

public sealed class TransferConflictProbeService : ITransferConflictProbeService
{
    private readonly IAuthenticationService _authenticationService;

    public TransferConflictProbeService(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    public async Task<bool> HasConflictAsync(TransferDirection direction, string localPath, SharePath remotePath, CancellationToken cancellationToken)
    {
        return direction switch
        {
            TransferDirection.Upload => await RemoteFileExistsAsync(remotePath, cancellationToken).ConfigureAwait(false),
            TransferDirection.Download => File.Exists(localPath),
            _ => false
        };
    }

    public async Task<(string LocalPath, SharePath RemotePath)> ResolveRenameTargetAsync(
        TransferDirection direction,
        string localPath,
        SharePath remotePath,
        CancellationToken cancellationToken)
    {
        if (direction == TransferDirection.Upload)
        {
            var renamedRemote = await ResolveRemoteRenameAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return (localPath, renamedRemote);
        }

        var renamedLocal = ResolveLocalRename(localPath);
        return (renamedLocal, remotePath);
    }

    private string ResolveLocalRename(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(localPath);
        var extension = Path.GetExtension(localPath);
        var sequence = 1;

        while (true)
        {
            var candidateName = $"{fileName} ({sequence}){extension}";
            var candidatePath = string.IsNullOrWhiteSpace(directory)
                ? candidateName
                : Path.Combine(directory, candidateName);
            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }

            sequence++;
        }
    }

    private async Task<SharePath> ResolveRemoteRenameAsync(SharePath remotePath, CancellationToken cancellationToken)
    {
        var normalized = remotePath.NormalizeRelativePath();
        var directoryPart = string.Empty;
        var filePart = normalized;
        var slash = normalized.LastIndexOf('/');
        if (slash >= 0)
        {
            directoryPart = normalized[..slash];
            filePart = normalized[(slash + 1)..];
        }

        var fileName = Path.GetFileNameWithoutExtension(filePart);
        var extension = Path.GetExtension(filePart);
        var sequence = 1;

        while (true)
        {
            var candidateName = $"{fileName} ({sequence}){extension}";
            var candidateRelative = string.IsNullOrWhiteSpace(directoryPart)
                ? candidateName
                : $"{directoryPart}/{candidateName}";
            var candidatePath = new SharePath(remotePath.StorageAccountName, remotePath.ShareName, candidateRelative);
            if (!await RemoteFileExistsAsync(candidatePath, cancellationToken).ConfigureAwait(false))
            {
                return candidatePath;
            }

            sequence++;
        }
    }

    private async Task<bool> RemoteFileExistsAsync(SharePath path, CancellationToken cancellationToken)
    {
        var fileClient = GetRemoteFileClient(path);
        return await fileClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
    }

    private ShareFileClient GetRemoteFileClient(SharePath path)
    {
        var serviceClient = new ShareServiceClient(
            new Uri($"https://{path.StorageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });

        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var normalized = path.NormalizeRelativePath();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directoryClient = shareClient.GetRootDirectoryClient();
        foreach (var segment in segments.Take(Math.Max(0, segments.Length - 1)))
        {
            directoryClient = directoryClient.GetSubdirectoryClient(segment);
        }

        var fileName = segments.LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Remote file path is missing a file name.");
        }

        return directoryClient.GetFileClient(fileName);
    }
}
