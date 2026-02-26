using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;

namespace AzureFilesSync.Infrastructure.Transfers;

public sealed class AzureFileTransferExecutor : ITransferExecutor
{
    private static readonly HashSet<int> TransientStatuses = [408, 429, 500, 502, 503, 504];
    public const string UnresolvedAskConflictMessage = "Transfer canceled: conflict requires user decision, but Ask policy was unresolved at queue time.";

    private readonly IAuthenticationService _authenticationService;
    private readonly ICheckpointStore _checkpointStore;
    private readonly AzureClientOptions _options;

    public AzureFileTransferExecutor(IAuthenticationService authenticationService, ICheckpointStore checkpointStore, AzureClientOptions options)
    {
        _authenticationService = authenticationService;
        _checkpointStore = checkpointStore;
        _options = options;
    }

    public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        if (request.Direction == TransferDirection.Upload)
        {
            return Task.FromResult(new FileInfo(request.LocalPath).Length);
        }

        return GetRemoteLengthAsync(request, cancellationToken);
    }

    public async Task ExecuteAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        if (request.Direction == TransferDirection.Upload)
        {
            await UploadAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UploadAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(request.LocalPath);
        var totalBytes = fileInfo.Length;
        var chunkSize = ResolveChunkSize(request);
        var maxConcurrency = ResolveConcurrency(request);
        var maxBytesPerSecond = ResolveMaxBytesPerSecond(request);
        var throttler = new TransferRateLimiter(maxBytesPerSecond);

        var offset = checkpoint?.NextOffset ?? 0;
        if (checkpoint is not null && checkpoint.TotalBytes != totalBytes)
        {
            offset = 0;
        }

        offset = Math.Clamp(offset, 0, totalBytes);

        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        if (request.ConflictPolicy == TransferConflictPolicy.Ask &&
            await fileClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(UnresolvedAskConflictMessage);
        }

        await EnsureRemoteUploadFileAsync(fileClient, totalBytes, forceCreate: offset == 0, cancellationToken).ConfigureAwait(false);

        // Resume uses a linear checkpoint offset; keep resumed jobs sequential.
        if (offset > 0)
        {
            maxConcurrency = 1;
        }

        if (maxConcurrency <= 1)
        {
            await UploadSequentialAsync(jobId, request, fileClient, totalBytes, offset, chunkSize, throttler, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await UploadParallelAsync(request, fileClient, totalBytes, chunkSize, maxConcurrency, throttler, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadSequentialAsync(
        Guid jobId,
        TransferRequest request,
        ShareFileClient fileClient,
        long totalBytes,
        long startOffset,
        int chunkSize,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var offset = startOffset;
        await using var input = new FileStream(request.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true);
        input.Seek(offset, SeekOrigin.Begin);

        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = (int)Math.Min(chunkSize, totalBytes - offset);
            var buffer = new byte[chunkLength];
            var read = await input.ReadAsync(buffer.AsMemory(0, chunkLength), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await throttler.WaitAsync(read, cancellationToken).ConfigureAwait(false);
            await ExecuteWithRetryAsync(
                async () =>
                {
                    await using var retryStream = new MemoryStream(buffer, 0, read, writable: false);
                    await fileClient.UploadRangeAsync(new HttpRange(offset, read), retryStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            offset += read;
            progress(new TransferProgress(offset, totalBytes));
            await _checkpointStore.SaveAsync(new TransferCheckpoint(jobId, request.Direction, request.LocalPath, request.RemotePath, totalBytes, offset, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UploadParallelAsync(
        TransferRequest request,
        ShareFileClient fileClient,
        long totalBytes,
        int chunkSize,
        int maxConcurrency,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        long completedBytes = 0;
        var ranges = BuildRanges(totalBytes, chunkSize);
        using var fileHandle = File.OpenHandle(request.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxConcurrency
        };

        await Parallel.ForEachAsync(ranges, parallelOptions, async (range, ct) =>
        {
            var buffer = new byte[range.Length];
            var readTotal = 0;
            while (readTotal < range.Length)
            {
                var read = await RandomAccess.ReadAsync(fileHandle, buffer.AsMemory(readTotal, range.Length - readTotal), range.Offset + readTotal, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                readTotal += read;
            }

            if (readTotal <= 0)
            {
                return;
            }

            await throttler.WaitAsync(readTotal, ct).ConfigureAwait(false);
            await ExecuteWithRetryAsync(
                async () =>
                {
                    await using var payload = new MemoryStream(buffer, 0, readTotal, writable: false);
                    await fileClient.UploadRangeAsync(new HttpRange(range.Offset, readTotal), payload, cancellationToken: ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            var totalDone = Interlocked.Add(ref completedBytes, readTotal);
            progress(new TransferProgress(totalDone, totalBytes));
        }).ConfigureAwait(false);
    }

    private async Task DownloadAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var properties = await ExecuteWithRetryAsync(
            () => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var totalBytes = properties.Value.ContentLength;
        var chunkSize = ResolveChunkSize(request);
        var maxConcurrency = ResolveConcurrency(request);
        var maxBytesPerSecond = ResolveMaxBytesPerSecond(request);
        var throttler = new TransferRateLimiter(maxBytesPerSecond);

        var directory = Path.GetDirectoryName(request.LocalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (request.ConflictPolicy == TransferConflictPolicy.Ask && File.Exists(request.LocalPath))
        {
            throw new InvalidOperationException(UnresolvedAskConflictMessage);
        }

        var existingLength = File.Exists(request.LocalPath) ? new FileInfo(request.LocalPath).Length : 0;
        var checkpointOffset = checkpoint?.NextOffset ?? 0;
        var offset = checkpoint is null ? 0 : Math.Min(Math.Min(checkpointOffset, existingLength), totalBytes);

        // Resume uses a linear checkpoint offset; keep resumed jobs sequential.
        if (offset > 0)
        {
            maxConcurrency = 1;
        }

        if (maxConcurrency <= 1)
        {
            await DownloadSequentialAsync(jobId, request, fileClient, totalBytes, offset, chunkSize, throttler, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadParallelAsync(request, fileClient, totalBytes, chunkSize, maxConcurrency, throttler, progress, cancellationToken).ConfigureAwait(false);
        }

        if (properties.Value.ContentHash is { Length: > 0 } remoteHash)
        {
            var localHash = ComputeMd5(request.LocalPath);
            if (!localHash.SequenceEqual(remoteHash))
            {
                throw new InvalidOperationException("Downloaded file hash does not match remote content hash.");
            }
        }
    }

    private async Task DownloadSequentialAsync(
        Guid jobId,
        TransferRequest request,
        ShareFileClient fileClient,
        long totalBytes,
        long startOffset,
        int chunkSize,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var offset = startOffset;
        await using var output = new FileStream(request.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, chunkSize, useAsync: true);
        output.SetLength(totalBytes);

        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(chunkSize, totalBytes - offset);
            var response = await ExecuteWithRetryAsync(
                () => fileClient.DownloadAsync(new ShareFileDownloadOptions { Range = new HttpRange(offset, chunkLength) }, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await using var remoteStream = response.Value.Content;
            output.Seek(offset, SeekOrigin.Begin);
            await remoteStream.CopyToAsync(output, chunkSize, cancellationToken).ConfigureAwait(false);

            var transferred = (int)chunkLength;
            await throttler.WaitAsync(transferred, cancellationToken).ConfigureAwait(false);
            offset += chunkLength;
            progress(new TransferProgress(offset, totalBytes));
            await _checkpointStore.SaveAsync(new TransferCheckpoint(jobId, request.Direction, request.LocalPath, request.RemotePath, totalBytes, offset, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadParallelAsync(
        TransferRequest request,
        ShareFileClient fileClient,
        long totalBytes,
        int chunkSize,
        int maxConcurrency,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        long completedBytes = 0;
        var ranges = BuildRanges(totalBytes, chunkSize);
        using var outputHandle = File.OpenHandle(request.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RandomAccess.SetLength(outputHandle, totalBytes);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxConcurrency
        };

        await Parallel.ForEachAsync(ranges, parallelOptions, async (range, ct) =>
        {
            await throttler.WaitAsync(range.Length, ct).ConfigureAwait(false);
            var response = await ExecuteWithRetryAsync(
                () => fileClient.DownloadAsync(new ShareFileDownloadOptions { Range = new HttpRange(range.Offset, range.Length) }, ct),
                ct).ConfigureAwait(false);

            await using var remoteStream = response.Value.Content;
            var buffer = new byte[range.Length];
            var copied = 0;
            while (copied < range.Length)
            {
                var read = await remoteStream.ReadAsync(buffer.AsMemory(copied, range.Length - copied), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied += read;
            }

            if (copied <= 0)
            {
                return;
            }

            await RandomAccess.WriteAsync(outputHandle, buffer.AsMemory(0, copied), range.Offset, ct).ConfigureAwait(false);
            var totalDone = Interlocked.Add(ref completedBytes, copied);
            progress(new TransferProgress(totalDone, totalBytes));
        }).ConfigureAwait(false);
    }

    private async Task<long> GetRemoteLengthAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var props = await ExecuteWithRetryAsync(() => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        return props.Value.ContentLength;
    }

    private async Task<ShareFileClient> GetRemoteFileClientAsync(SharePath path, CancellationToken cancellationToken)
    {
        var serviceClient = new ShareServiceClient(
            new Uri($"https://{path.StorageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });
        var shareClient = serviceClient.GetShareClient(path.ShareName);
        await ExecuteWithRetryAsync(() => shareClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

        var normalized = path.NormalizeRelativePath();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        ShareDirectoryClient directoryClient = shareClient.GetRootDirectoryClient();
        foreach (var segment in segments.Take(Math.Max(0, segments.Length - 1)))
        {
            directoryClient = directoryClient.GetSubdirectoryClient(segment);
            await ExecuteWithRetryAsync(() => directoryClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var fileName = segments.LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Remote file path is missing a file name.");
        }

        return directoryClient.GetFileClient(fileName);
    }

    private async Task EnsureRemoteUploadFileAsync(ShareFileClient fileClient, long fileLength, bool forceCreate, CancellationToken cancellationToken)
    {
        if (forceCreate)
        {
            await ExecuteWithRetryAsync(() => fileClient.CreateAsync(fileLength, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var props = await ExecuteWithRetryAsync(() => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
            if (props.Value.ContentLength != fileLength)
            {
                await ExecuteWithRetryAsync(() => fileClient.CreateAsync(fileLength, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            await ExecuteWithRetryAsync(() => fileClient.CreateAsync(fileLength, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteWithRetryAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Math.Max(0, _options.TransferRetryAttempts - 1) && IsTransient(ex))
            {
                attempt++;
                var delayMs = _options.TransferRetryBaseDelayMs * attempt;
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        return ex switch
        {
            RequestFailedException requestFailed => TransientStatuses.Contains(requestFailed.Status),
            IOException => true,
            TimeoutException => true,
            _ => false
        };
    }

    private static byte[] ComputeMd5(string path)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return md5.ComputeHash(stream);
    }

    private int ResolveChunkSize(TransferRequest request)
    {
        var requested = request.ChunkSizeBytes > 0 ? request.ChunkSizeBytes : _options.TransferChunkSizeBytes;
        return Math.Clamp(requested, 64 * 1024, 32 * 1024 * 1024);
    }

    private int ResolveConcurrency(TransferRequest request)
    {
        var requested = request.MaxConcurrency > 0 ? request.MaxConcurrency : _options.TransferConcurrency;
        return Math.Clamp(requested, 1, 32);
    }

    private int ResolveMaxBytesPerSecond(TransferRequest request)
    {
        var requested = request.MaxBytesPerSecond > 0 ? request.MaxBytesPerSecond : _options.TransferMaxBytesPerSecond;
        return Math.Max(0, requested);
    }

    private static IEnumerable<TransferRange> BuildRanges(long totalBytes, int chunkSize)
    {
        for (long offset = 0; offset < totalBytes; offset += chunkSize)
        {
            yield return new TransferRange(offset, (int)Math.Min(chunkSize, totalBytes - offset));
        }
    }

    private readonly record struct TransferRange(long Offset, int Length);

    private sealed class TransferRateLimiter
    {
        private readonly int _maxBytesPerSecond;
        private readonly Lock _lock = new();
        private double _nextWindowSeconds;

        public TransferRateLimiter(int maxBytesPerSecond)
        {
            _maxBytesPerSecond = maxBytesPerSecond;
            _nextWindowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
        {
            if (_maxBytesPerSecond <= 0 || bytes <= 0)
            {
                return;
            }

            var nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double delaySeconds;
            lock (_lock)
            {
                var start = Math.Max(nowSeconds, _nextWindowSeconds);
                _nextWindowSeconds = start + (bytes / (double)_maxBytesPerSecond);
                delaySeconds = Math.Max(0, start - nowSeconds);
            }

            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
