using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using Azure;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class RemoteSearchService : IRemoteSearchService
{
    private const int PageSize = 500;
    private const int DefaultMaxResults = 1000;
    private const int EmitMatchBatchSize = 50;
    private static readonly TimeSpan EmitInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DirectoryPageTimeout = TimeSpan.FromSeconds(8);

    private readonly IAzureFilesBrowserService _browserService;
    private readonly ILogger<RemoteSearchService> _logger;

    public RemoteSearchService(
        IAzureFilesBrowserService browserService,
        ILogger<RemoteSearchService> logger)
    {
        _browserService = browserService;
        _logger = logger;
    }

    public async IAsyncEnumerable<RemoteSearchProgress> SearchIncrementalAsync(
        RemoteSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return new RemoteSearchProgress([], 0, IsCompleted: true, IsTruncated: false, 0, 0, []);
            yield break;
        }

        var maxResults = request.MaxResults <= 0 ? DefaultMaxResults : request.MaxResults;
        var includeDirectories = request.IncludeDirectories;
        var startRelativePath = request.StartPath.NormalizeRelativePath();

        var matches = new List<RemoteEntry>();
        var pendingMatches = new List<RemoteEntry>();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(startRelativePath);
        var lastEmitUtc = DateTimeOffset.UtcNow;

        var scannedDirectories = 0;
        var scannedEntries = 0;
        var scannedPages = 0;

        var reachedResultCap = false;

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentRelativePath = pendingDirectories.Dequeue();
            if (!visitedDirectories.Add(currentRelativePath))
            {
                continue;
            }

            scannedDirectories++;
            var beforeDirectoryCallUtc = DateTimeOffset.UtcNow;
            if (scannedDirectories == 1 || beforeDirectoryCallUtc - lastEmitUtc >= EmitInterval)
            {
                yield return new RemoteSearchProgress(
                    [],
                    matches.Count,
                    IsCompleted: false,
                    IsTruncated: false,
                    scannedDirectories,
                    scannedEntries,
                    matches.ToList());
                lastEmitUtc = beforeDirectoryCallUtc;
            }

            string? continuation = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                RemoteDirectoryPage page;
                try
                {
                    using var directoryPageTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var pagePath = new SharePath(
                        request.StartPath.StorageAccountName,
                        request.StartPath.ShareName,
                        currentRelativePath,
                        request.StartPath.ProviderKind);
                    var pageTimer = Stopwatch.StartNew();
                    var pageTask = _browserService.ListDirectoryPageAsync(
                        pagePath,
                        continuation,
                        PageSize,
                        directoryPageTimeoutCts.Token);
                    var timeoutTask = Task.Delay(DirectoryPageTimeout, cancellationToken);
                    _logger.LogDebug(
                        "Remote search listing directory page started. Directory={Directory} ContinuationTokenPresent={HasContinuation} TimeoutSeconds={TimeoutSeconds}",
                        string.IsNullOrWhiteSpace(currentRelativePath) ? "<root>" : currentRelativePath,
                        !string.IsNullOrWhiteSpace(continuation),
                        DirectoryPageTimeout.TotalSeconds);
                    var completedTask = await Task.WhenAny(pageTask, timeoutTask).ConfigureAwait(false);
                    if (!ReferenceEquals(completedTask, pageTask))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        directoryPageTimeoutCts.Cancel();
                        _logger.LogWarning(
                            "Remote search skipped directory page after watchdog timeout. Directory={Directory} ContinuationTokenPresent={HasContinuation} TimeoutSeconds={TimeoutSeconds}",
                            string.IsNullOrWhiteSpace(currentRelativePath) ? "<root>" : currentRelativePath,
                            !string.IsNullOrWhiteSpace(continuation),
                            DirectoryPageTimeout.TotalSeconds);
                        break;
                    }

                    page = await pageTask.ConfigureAwait(false);
                    scannedPages++;
                    pageTimer.Stop();
                    _logger.LogDebug(
                        "Remote search listed directory page. Directory={Directory} ContinuationTokenPresent={HasContinuation} Entries={Entries} NextContinuationTokenPresent={HasNextContinuation} ElapsedMs={ElapsedMs} ScannedPages={ScannedPages} ScannedDirectories={ScannedDirectories} ScannedEntries={ScannedEntries}",
                        string.IsNullOrWhiteSpace(currentRelativePath) ? "<root>" : currentRelativePath,
                        !string.IsNullOrWhiteSpace(continuation),
                        page.Entries.Count,
                        !string.IsNullOrWhiteSpace(page.ContinuationToken),
                        pageTimer.ElapsedMilliseconds,
                        scannedPages,
                        scannedDirectories,
                        scannedEntries);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                        !string.IsNullOrWhiteSpace(currentRelativePath))
                {
                    _logger.LogWarning(
                        "Remote search skipped directory after timeout. Directory={Directory} TimeoutSeconds={TimeoutSeconds}",
                        currentRelativePath,
                        DirectoryPageTimeout.TotalSeconds);
                    break;
                }
                catch (Exception ex) when (ShouldSkipDirectoryFailure(ex, currentRelativePath))
                {
                    // A single restricted/missing child path should not abort the whole recursive search.
                    _logger.LogWarning(
                        ex,
                        "Remote search skipped inaccessible directory. Directory={Directory}",
                        currentRelativePath);
                    break;
                }

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

                    var isMatch =
                        entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        entry.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!isMatch)
                    {
                        continue;
                    }

                    if (matches.Count < maxResults)
                    {
                        matches.Add(entry);
                        pendingMatches.Add(entry);
                    }
                    else
                    {
                        reachedResultCap = true;
                    }

                    var nowUtc = DateTimeOffset.UtcNow;
                    if (pendingMatches.Count >= EmitMatchBatchSize || nowUtc - lastEmitUtc >= EmitInterval)
                    {
                        yield return new RemoteSearchProgress(
                            pendingMatches.ToList(),
                            matches.Count,
                            IsCompleted: false,
                            IsTruncated: false,
                            scannedDirectories,
                            scannedEntries,
                            matches.ToList());
                        pendingMatches.Clear();
                        lastEmitUtc = nowUtc;
                    }
                }

                var heartbeatUtc = DateTimeOffset.UtcNow;
                if (pendingMatches.Count > 0 && heartbeatUtc - lastEmitUtc >= EmitInterval)
                {
                    // Flush partial match batches even when no additional matches arrive.
                    yield return new RemoteSearchProgress(
                        pendingMatches.ToList(),
                        matches.Count,
                        IsCompleted: false,
                        IsTruncated: false,
                        scannedDirectories,
                        scannedEntries,
                        matches.ToList());
                    pendingMatches.Clear();
                    lastEmitUtc = heartbeatUtc;
                }

                if (pendingMatches.Count == 0 && heartbeatUtc - lastEmitUtc >= EmitInterval)
                {
                    yield return new RemoteSearchProgress(
                        [],
                        matches.Count,
                        IsCompleted: false,
                        IsTruncated: false,
                        scannedDirectories,
                        scannedEntries,
                        matches.ToList());
                    lastEmitUtc = heartbeatUtc;
                }

                continuation = page.ContinuationToken;
            }
            while (!string.IsNullOrWhiteSpace(continuation));
        }

        yield return new RemoteSearchProgress(
            pendingMatches.ToList(),
            matches.Count,
            IsCompleted: true,
            IsTruncated: reachedResultCap,
            scannedDirectories,
            scannedEntries,
            matches.ToList());
    }

    private static bool ShouldSkipDirectoryFailure(Exception ex, string currentRelativePath)
    {
        if (string.IsNullOrWhiteSpace(currentRelativePath))
        {
            return false;
        }

        if (ex is AggregateException aggregate)
        {
            var flattened = aggregate.Flatten().InnerExceptions;
            return flattened.Count > 0 && flattened.All(inner => ShouldSkipDirectoryFailure(inner, currentRelativePath));
        }

        if (ex is not RequestFailedException requestFailed)
        {
            return false;
        }

        if (requestFailed.Status is 401 or 403 or 404)
        {
            return true;
        }

        return requestFailed.ErrorCode switch
        {
            "AuthorizationFailure" => true,
            "AuthorizationPermissionMismatch" => true,
            "AccessDenied" => true,
            "PathNotFound" => true,
            "ResourceNotFound" => true,
            _ => false
        };
    }
}
