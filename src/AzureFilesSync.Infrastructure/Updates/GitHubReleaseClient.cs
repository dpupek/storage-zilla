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

    public async Task<GitHubRelease?> GetLatestReleaseAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{_options.Owner}/{_options.Repo}/releases?per_page=30";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        GitHubRelease? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (!TryParseRelease(release, out var parsed))
            {
                continue;
            }

            if (parsed.IsDraft)
            {
                continue;
            }

            var channelMatch = channel == UpdateChannel.Beta ? parsed.IsPrerelease : !parsed.IsPrerelease;
            if (!channelMatch)
            {
                continue;
            }

            if (best is null || parsed.PublishedAtUtc > best.PublishedAtUtc)
            {
                best = parsed;
            }
        }

        return best;
    }

    private static bool TryParseRelease(JsonElement root, out GitHubRelease release)
    {
        release = default!;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlElement) ? htmlUrlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var isDraft = root.TryGetProperty("draft", out var draftElement) && draftElement.GetBoolean();
        var isPrerelease = root.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.GetBoolean();
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

        release = new GitHubRelease(tag, htmlUrl ?? string.Empty, isPrerelease, isDraft, publishedAt, assets);
        return true;
    }
}
