namespace AzureFilesSync.Core.Models;

public enum UpdateChannel
{
    Stable = 0,
    Beta = 1
}

public enum UpdateStatus
{
    Idle,
    Checking,
    Available,
    NotAvailable,
    Downloading,
    Downloaded,
    ValidationFailed,
    ReadyToInstall,
    Failed
}

public sealed record GitHubReleaseAsset(
    string Name,
    string DownloadUrl,
    long SizeBytes);

public sealed record GitHubRelease(
    string TagName,
    bool IsPrerelease,
    bool IsDraft,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed record UpdateCandidate(
    string Version,
    string Tag,
    DateTimeOffset PublishedAtUtc,
    string MsixAssetName,
    string MsixAssetUrl,
    string Sha256AssetUrl);

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    UpdateCandidate? Candidate,
    string Message);

public sealed record UpdateDownloadResult(
    UpdateCandidate Candidate,
    string LocalFilePath,
    string ExpectedSha256,
    string ActualSha256,
    DateTimeOffset DownloadedAtUtc);

public sealed record UpdateValidationResult(
    bool IsValid,
    string? Publisher,
    string? PackageVersion,
    string? Error);
