namespace AzureFilesSync.Infrastructure.Azure;

internal static class BlobHierarchyPaths
{
    public const string DirectoryMarkerFileName = ".storage-zilla-folder";

    public static string NormalizePrefix(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return $"{relativePath.Replace('\\', '/').Trim('/')}/";
    }

    public static string BuildDirectoryMarkerPath(string? relativePath)
    {
        var normalized = relativePath?.Replace('\\', '/').Trim('/') ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? DirectoryMarkerFileName
            : $"{normalized}/{DirectoryMarkerFileName}";
    }

    public static bool IsDirectoryMarkerBlob(string blobName)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            return false;
        }

        return blobName.EndsWith($"/{DirectoryMarkerFileName}", StringComparison.OrdinalIgnoreCase)
               || string.Equals(blobName, DirectoryMarkerFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetLeafName(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }
}
