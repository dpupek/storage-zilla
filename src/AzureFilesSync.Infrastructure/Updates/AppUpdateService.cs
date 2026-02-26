using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;

namespace AzureFilesSync.Infrastructure.Updates;

public sealed class AppUpdateService : IAppUpdateService
{
    private readonly IGitHubReleaseClient _releaseClient;
    private readonly HttpClient _httpClient;
    private readonly UpdateOptions _options;
    private readonly string _updateRoot;
    private UpdateChannel _currentChannel;

    public AppUpdateService(IGitHubReleaseClient releaseClient, HttpClient httpClient, UpdateOptions options)
    {
        _releaseClient = releaseClient;
        _httpClient = httpClient;
        _options = options;
        _currentChannel = options.DefaultChannel;
        _updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzureFilesSync",
            "updates");
    }

    public UpdateChannel CurrentChannel => _currentChannel;

    public void SetChannel(UpdateChannel channel)
    {
        _currentChannel = channel == UpdateChannel.Beta && !_options.AllowBetaChannel
            ? UpdateChannel.Stable
            : channel;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var currentVersion = GetCurrentVersion();
        var release = await _releaseClient.GetLatestReleaseAsync(_currentChannel, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            var channelLabel = _currentChannel == UpdateChannel.Beta ? "beta" : "stable";
            return new UpdateCheckResult(currentVersion, null, false, null, $"Unable to retrieve the latest {channelLabel} release.");
        }

        var latestVersion = NormalizeTagToVersion(release.TagName);
        if (!TryParseVersion(currentVersion, out var current) || !TryParseVersion(latestVersion, out var latest))
        {
            return new UpdateCheckResult(
                currentVersion,
                latestVersion,
                false,
                null,
                "Could not compare current version to latest release.");
        }

        var msixAsset = release.Assets.FirstOrDefault(x =>
            x.Name.EndsWith(_options.ReleaseAssetExtension, StringComparison.OrdinalIgnoreCase));
        var hashAsset = release.Assets.FirstOrDefault(x =>
            string.Equals(x.Name, _options.Sha256FileName, StringComparison.OrdinalIgnoreCase));
        if (msixAsset is null || hashAsset is null)
        {
            return new UpdateCheckResult(currentVersion, latestVersion, false, null, "Latest release is missing installer assets.");
        }

        if (latest <= current)
        {
            return new UpdateCheckResult(currentVersion, latestVersion, false, null, $"You're up to date ({currentVersion}).");
        }

        var candidate = new UpdateCandidate(
            latestVersion,
            release.TagName,
            release.PublishedAtUtc,
            msixAsset.Name,
            msixAsset.DownloadUrl,
            hashAsset.DownloadUrl);
        return new UpdateCheckResult(currentVersion, latestVersion, true, candidate, $"Update available: {latestVersion}");
    }

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(UpdateCandidate candidate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_updateRoot);
        var updateDir = Path.Combine(_updateRoot, candidate.Version);
        Directory.CreateDirectory(updateDir);

        var shaPath = Path.Combine(updateDir, _options.Sha256FileName);
        await DownloadFileAsync(candidate.Sha256AssetUrl, shaPath, progress: null, cancellationToken).ConfigureAwait(false);
        var expectedSha = ParseExpectedHash(await File.ReadAllTextAsync(shaPath, cancellationToken).ConfigureAwait(false), candidate.MsixAssetName);

        var installerPath = Path.Combine(updateDir, candidate.MsixAssetName);
        await DownloadFileAsync(candidate.MsixAssetUrl, installerPath, progress, cancellationToken).ConfigureAwait(false);
        var actualSha = ComputeSha256(installerPath);

        return new UpdateDownloadResult(candidate, installerPath, expectedSha, actualSha, DateTimeOffset.UtcNow);
    }

    public Task<UpdateValidationResult> ValidateDownloadedUpdateAsync(UpdateDownloadResult downloaded, CancellationToken cancellationToken)
    {
        if (!string.Equals(downloaded.ExpectedSha256, downloaded.ActualSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new UpdateValidationResult(false, null, null, "Downloaded installer hash did not match release checksum."));
        }

        try
        {
            using var archive = ZipFile.OpenRead(downloaded.LocalFilePath);
            var manifestEntry = archive.GetEntry("AppxManifest.xml");
            if (manifestEntry is null)
            {
                return Task.FromResult(new UpdateValidationResult(false, null, null, "MSIX manifest was not found."));
            }

            using var stream = manifestEntry.Open();
            var xml = XDocument.Load(stream);
            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
            var identity = xml.Root?.Element(ns + "Identity");
            if (identity is null)
            {
                return Task.FromResult(new UpdateValidationResult(false, null, null, "MSIX identity information was not found."));
            }

            var publisher = identity.Attribute("Publisher")?.Value;
            var packageVersion = identity.Attribute("Version")?.Value;
            if (!string.Equals(publisher, _options.ExpectedPublisher, StringComparison.Ordinal))
            {
                return Task.FromResult(new UpdateValidationResult(false, publisher, packageVersion, "MSIX publisher does not match expected publisher."));
            }

            if (string.IsNullOrWhiteSpace(packageVersion) ||
                !packageVersion.StartsWith(downloaded.Candidate.Version + ".", StringComparison.Ordinal))
            {
                return Task.FromResult(new UpdateValidationResult(false, publisher, packageVersion, "MSIX package version does not match release version."));
            }

            return Task.FromResult(new UpdateValidationResult(true, publisher, packageVersion, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new UpdateValidationResult(false, null, null, $"Failed to validate downloaded update: {ex.Message}"));
        }
    }

    public Task LaunchInstallerAsync(UpdateDownloadResult downloaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo
        {
            FileName = downloaded.LocalFilePath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    private async Task DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            read += bytesRead;
            if (total.HasValue && total.Value > 0)
            {
                progress?.Report((double)read / total.Value);
            }
        }
    }

    private static string ParseExpectedHash(string checksumContents, string assetName)
    {
        var pattern = $@"^([a-fA-F0-9]{{64}})\s+\*?{Regex.Escape(assetName)}\s*$";
        foreach (var line in checksumContents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line.Trim(), pattern);
            if (match.Success)
            {
                return match.Groups[1].Value.ToLowerInvariant();
            }
        }

        throw new InvalidOperationException($"Unable to locate checksum for asset '{assetName}'.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetCurrentVersion()
    {
        var entryPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(entryPath))
        {
            var version = FileVersionInfo.GetVersionInfo(entryPath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version.Split('+')[0];
            }
        }

        var fallback = typeof(AppUpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return fallback.Split('+')[0];
    }

    private static bool TryParseVersion(string version, out Version parsed)
    {
        var cleaned = version.Split('-', '+')[0];
        return Version.TryParse(cleaned, out parsed!);
    }

    private static string NormalizeTagToVersion(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
}
