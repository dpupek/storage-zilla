using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

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
        var serviceClient = new ShareServiceClient(
            new Uri($"https://{path.StorageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        var directoryClient = string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
            ? shareClient.GetRootDirectoryClient()
            : shareClient.GetDirectoryClient(path.NormalizeRelativePath());

        var results = new List<RemoteEntry>();
        await foreach (ShareFileItem item in directoryClient.GetFilesAndDirectoriesAsync(cancellationToken: cancellationToken))
        {
            var fullPath = string.IsNullOrWhiteSpace(path.NormalizeRelativePath())
                ? item.Name
                : $"{path.NormalizeRelativePath()}/{item.Name}";

            results.Add(new RemoteEntry(
                item.Name,
                fullPath,
                item.IsDirectory,
                item.FileSize ?? 0,
                item.Properties.LastModified,
                CreatedTime: TryGetDateTimeOffset(item.Properties, "CreatedOn", "FileCreatedOn"),
                Author: TryGetString(item.Properties, "FilePermissionKey")));
        }

        return results;
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
}
