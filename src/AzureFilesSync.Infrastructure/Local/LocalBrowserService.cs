using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace AzureFilesSync.Infrastructure.Local;

public sealed class LocalBrowserService : ILocalBrowserService
{
    public Task<IReadOnlyList<LocalEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult<IReadOnlyList<LocalEntry>>([]);
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return Task.FromResult<IReadOnlyList<LocalEntry>>([]);
        }

        var entries = directory
            .EnumerateFileSystemInfos()
            .Select(info =>
            {
                var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
                var length = info is FileInfo file ? file.Length : 0;
                return new LocalEntry(
                    info.Name,
                    info.FullName,
                    isDirectory,
                    length,
                    info.LastWriteTimeUtc,
                    info.CreationTimeUtc,
                    GetOwnerSafe(info.FullName, isDirectory));
            })
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<LocalEntry>>(entries);
    }

    public Task<LocalEntry?> GetEntryDetailsAsync(string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return Task.FromResult<LocalEntry?>(null);
        }

        FileSystemInfo? info = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : File.Exists(fullPath)
                ? new FileInfo(fullPath)
                : null;

        if (info is null)
        {
            return Task.FromResult<LocalEntry?>(null);
        }

        var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
        var length = info is FileInfo file ? file.Length : 0;
        var entry = new LocalEntry(
            info.Name,
            info.FullName,
            isDirectory,
            length,
            info.LastWriteTimeUtc,
            info.CreationTimeUtc,
            GetOwnerSafe(info.FullName, isDirectory));
        return Task.FromResult<LocalEntry?>(entry);
    }

    private static string? GetOwnerSafe(string path, bool isDirectory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            ObjectSecurity? security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            var sid = security?.GetOwner(typeof(SecurityIdentifier));
            return sid is null ? null : sid.Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            return null;
        }
    }
}
