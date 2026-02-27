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

    public async Task<RemoteEntry?> GetEntryDetailsAsync(SharePath path, CancellationToken cancellationToken)
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
