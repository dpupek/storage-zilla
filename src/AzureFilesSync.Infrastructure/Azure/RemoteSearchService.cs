using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class RemoteSearchService : IRemoteSearchService
{
    private const int PageSize = 500;
    private const int DefaultMaxResults = 1000;

    private readonly IAzureFilesBrowserService _browserService;

    public RemoteSearchService(IAzureFilesBrowserService browserService)
    {
        _browserService = browserService;
    }

    public async Task<RemoteSearchResult> SearchAsync(RemoteSearchRequest request, CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new RemoteSearchResult([], false, 0, 0);
        }

        var maxResults = request.MaxResults <= 0 ? DefaultMaxResults : request.MaxResults;
        var includeDirectories = request.IncludeDirectories;

        var matches = new List<RemoteEntry>();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(request.StartPath.NormalizeRelativePath());

        var scannedDirectories = 0;
        var scannedEntries = 0;

        while (pendingDirectories.Count > 0 && matches.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentRelativePath = pendingDirectories.Dequeue();
            if (!visitedDirectories.Add(currentRelativePath))
            {
                continue;
            }

            scannedDirectories++;

            string? continuation = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await _browserService
                    .ListDirectoryPageAsync(
                        new SharePath(
                            request.StartPath.StorageAccountName,
                            request.StartPath.ShareName,
                            currentRelativePath),
                        continuation,
                        PageSize,
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var entry in page.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedEntries++;

                    if (entry.IsDirectory)
                    {
                        pendingDirectories.Enqueue(entry.FullPath);
                        if (!includeDirectories)
                        {
                            continue;
                        }
                    }

                    if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add(entry);
                    if (matches.Count >= maxResults)
                    {
                        var hasMore = !string.IsNullOrWhiteSpace(page.ContinuationToken) || pendingDirectories.Count > 0;
                        return new RemoteSearchResult(matches, hasMore, scannedDirectories, scannedEntries);
                    }
                }

                continuation = page.ContinuationToken;
            }
            while (!string.IsNullOrWhiteSpace(continuation));
        }

        return new RemoteSearchResult(matches, false, scannedDirectories, scannedEntries);
    }
}
