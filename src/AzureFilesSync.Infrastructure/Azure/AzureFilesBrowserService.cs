using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using Azure;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class AzureFilesBrowserService : IAzureFilesBrowserService
{
    private readonly IAuthenticationService _authenticationService;

    public AzureFilesBrowserService(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(SharePath path, CancellationToken cancellationToken)
    {
        var allEntries = new List<RemoteEntry>();
        string? continuationToken = null;

        do
        {
            var page = await ListDirectoryPageAsync(path, continuationToken, 500, cancellationToken);
            allEntries.AddRange(page.Entries);
            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));

        return allEntries;
    }

    public async Task<RemoteDirectoryPage> ListDirectoryPageAsync(
        SharePath path,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return path.ProviderKind == RemoteProviderKind.AzureBlob
            ? await ListBlobDirectoryPageAsync(path, continuationToken, pageSize, cancellationToken).ConfigureAwait(false)
            : await ListFileShareDirectoryPageAsync(path, continuationToken, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken)
    {
        return path.ProviderKind == RemoteProviderKind.AzureBlob
            ? await GetBlobEntryDetailsAsync(path, cancellationToken).ConfigureAwait(false)
            : await GetFileShareEntryDetailsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteDirectoryPage> ListFileShareDirectoryPageAsync(
        SharePath path,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var serviceClient = new ShareServiceClient(
            new Uri($"https://{path.StorageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var normalizedPath = path.NormalizeRelativePath();
        var directoryClient = string.IsNullOrWhiteSpace(normalizedPath)
            ? shareClient.GetRootDirectoryClient()
            : shareClient.GetDirectoryClient(normalizedPath);

        await foreach (Page<ShareFileItem> page in directoryClient
                           .GetFilesAndDirectoriesAsync(cancellationToken: cancellationToken)
                           .AsPages(continuationToken, Math.Max(1, pageSize)))
        {
            var entries = page.Values
                .Select(item => CreateRemoteEntry(item, normalizedPath))
                .ToList();

            return new RemoteDirectoryPage(
                entries,
                page.ContinuationToken,
                !string.IsNullOrWhiteSpace(page.ContinuationToken));
        }

        return new RemoteDirectoryPage([], null, false);
    }

    private async Task<RemoteDirectoryPage> ListBlobDirectoryPageAsync(
        SharePath path,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var serviceClient = new BlobServiceClient(
            new Uri($"https://{path.StorageAccountName}.blob.core.windows.net"),
            _authenticationService.GetCredential());
        var containerClient = serviceClient.GetBlobContainerClient(path.ShareName);
        var prefix = BlobHierarchyPaths.NormalizePrefix(path.NormalizeRelativePath());

        await foreach (Page<BlobHierarchyItem> page in containerClient
                           .GetBlobsByHierarchyAsync(delimiter: "/", prefix: prefix, cancellationToken: cancellationToken)
                           .AsPages(continuationToken, Math.Max(1, pageSize)))
        {
            var entries = new List<RemoteEntry>(page.Values.Count);
            foreach (var item in page.Values)
            {
                if (item.IsPrefix)
                {
                    var directoryPath = (item.Prefix ?? string.Empty).TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(directoryPath))
                    {
                        continue;
                    }

                    entries.Add(new RemoteEntry(
                        BlobHierarchyPaths.GetLeafName(directoryPath),
                        directoryPath,
                        true,
                        0,
                        null));
                    continue;
                }

                var blob = item.Blob;
                if (blob is null || BlobHierarchyPaths.IsDirectoryMarkerBlob(blob.Name))
                {
                    continue;
                }

                var fullPath = blob.Name.Replace('\\', '/').Trim('/');
                entries.Add(new RemoteEntry(
                    BlobHierarchyPaths.GetLeafName(fullPath),
                    fullPath,
                    false,
                    blob.Properties.ContentLength ?? 0,
                    blob.Properties.LastModified,
                    TryGetDateTimeOffset(blob.Properties, "CreatedOn")));
            }

            return new RemoteDirectoryPage(
                entries,
                page.ContinuationToken,
                !string.IsNullOrWhiteSpace(page.ContinuationToken));
        }

        return new RemoteDirectoryPage([], null, false);
    }

    private async Task<RemoteEntry?> GetFileShareEntryDetailsAsync(SharePath path, CancellationToken cancellationToken)
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
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var directoryPath = normalized.Contains('/')
            ? normalized[..normalized.LastIndexOf('/')]
            : string.Empty;
        var name = normalized.Contains('/')
            ? normalized[(normalized.LastIndexOf('/') + 1)..]
            : normalized;

        var parent = string.IsNullOrWhiteSpace(directoryPath)
            ? shareClient.GetRootDirectoryClient()
            : shareClient.GetDirectoryClient(directoryPath);

        var directoryClient = parent.GetSubdirectoryClient(name);
        try
        {
            var dirProps = await directoryClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return new RemoteEntry(
                name,
                normalized,
                true,
                0,
                dirProps.Value.LastModified,
                TryGetDateTimeOffset(dirProps.Value, "CreatedOn", "FileCreatedOn"),
                TryGetString(dirProps.Value, "FilePermissionKey"));
        }
        catch
        {
            var fileClient = parent.GetFileClient(name);
            var fileProps = await fileClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return new RemoteEntry(
                name,
                normalized,
                false,
                fileProps.Value.ContentLength,
                fileProps.Value.LastModified,
                TryGetDateTimeOffset(fileProps.Value, "CreatedOn", "FileCreatedOn"),
                TryGetString(fileProps.Value, "FilePermissionKey"));
        }
    }

    private async Task<RemoteEntry?> GetBlobEntryDetailsAsync(SharePath path, CancellationToken cancellationToken)
    {
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var serviceClient = new BlobServiceClient(
            new Uri($"https://{path.StorageAccountName}.blob.core.windows.net"),
            _authenticationService.GetCredential());
        var containerClient = serviceClient.GetBlobContainerClient(path.ShareName);
        var blobClient = containerClient.GetBlobClient(normalized);

        if (await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RemoteEntry(
                BlobHierarchyPaths.GetLeafName(normalized),
                normalized,
                false,
                properties.Value.ContentLength,
                properties.Value.LastModified,
                TryGetDateTimeOffset(properties.Value, "CreatedOn"));
        }

        var prefix = BlobHierarchyPaths.NormalizePrefix(normalized);
        await foreach (var page in containerClient.GetBlobsByHierarchyAsync(delimiter: "/", prefix: prefix, cancellationToken: cancellationToken).AsPages(default, 1))
        {
            if (page.Values.Any())
            {
                return new RemoteEntry(
                    BlobHierarchyPaths.GetLeafName(normalized),
                    normalized,
                    true,
                    0,
                    null);
            }
        }

        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = source.GetType().GetProperty(propertyName);
            if (property is null)
            {
                continue;
            }

            var value = property.GetValue(source);
            if (value is DateTimeOffset dto)
            {
                return dto;
            }
        }

        return null;
    }

    private static string? TryGetString(object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = source.GetType().GetProperty(propertyName);
            if (property is null)
            {
                continue;
            }

            var value = property.GetValue(source) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static RemoteEntry CreateRemoteEntry(ShareFileItem item, string normalizedPath)
    {
        var fullPath = string.IsNullOrWhiteSpace(normalizedPath)
            ? item.Name
            : $"{normalizedPath}/{item.Name}";

        return new RemoteEntry(
            item.Name,
            fullPath,
            item.IsDirectory,
            item.FileSize ?? 0,
            item.Properties.LastModified,
            CreatedTime: TryGetDateTimeOffset(item.Properties, "CreatedOn", "FileCreatedOn"),
            Author: TryGetString(item.Properties, "FilePermissionKey"));
    }
}
