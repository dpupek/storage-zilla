using System.Text.Json;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;

namespace AzureFilesSync.Infrastructure.Updates;

public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private readonly HttpClient _httpClient;
    private readonly UpdateOptions _options;

    public GitHubReleaseClient(HttpClient httpClient, UpdateOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<GitHubRelease?> GetLatestStableReleaseAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{_options.Owner}/{_options.Repo}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var isDraft = root.GetProperty("draft").GetBoolean();
        var isPrerelease = root.GetProperty("prerelease").GetBoolean();
        if (isDraft || isPrerelease)
        {
            return null;
        }

        var publishedAt = root.TryGetProperty("published_at", out var publishedAtElement) &&
                          DateTimeOffset.TryParse(publishedAtElement.GetString(), out var parsedPublishedAt)
            ? parsedPublishedAt
            : DateTimeOffset.UtcNow;

        var assets = new List<GitHubReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsElement))
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                var size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                assets.Add(new GitHubReleaseAsset(name, downloadUrl, size));
            }
        }

        return new GitHubRelease(tag, isPrerelease, isDraft, publishedAt, assets);
    }
}
