namespace AzureFilesSync.Core.Models;

public sealed record LocalEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTimeOffset LastWriteTime,
    DateTimeOffset? CreatedTime = null,
    string? Author = null);

public sealed record RemoteEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTimeOffset? LastWriteTime,
    DateTimeOffset? CreatedTime = null,
    string? Author = null);

public sealed record RemoteDirectoryPage(
    IReadOnlyList<RemoteEntry> Entries,
    string? ContinuationToken,
    bool HasMore);

public sealed record SharePath(string StorageAccountName, string ShareName, string RelativePath)
{
    public string NormalizeRelativePath()
    {
        var trimmed = RelativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : trimmed;
    }
}
