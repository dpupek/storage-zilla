using AzureFilesSync.Core.Contracts;

namespace AzureFilesSync.Core.Services;

public sealed class PathDisplayFormatter : IPathDisplayFormatter
{
    public string NormalizeLocalPath(string? path, string fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path.Trim();
        }

        return fallbackPath;
    }

    public string NormalizeRemotePathDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed is @"\" or "/" or "//")
        {
            return string.Empty;
        }

        return trimmed.Replace('\\', '/').Trim('/');
    }

    public string FormatRemotePathDisplay(string? path)
    {
        var normalized = NormalizeRemotePathDisplay(path);
        return string.IsNullOrWhiteSpace(normalized)
            ? "//"
            : $"//{normalized}";
    }
}
