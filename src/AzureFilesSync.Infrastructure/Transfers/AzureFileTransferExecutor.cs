using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Core.Services;
using AzureFilesSync.Infrastructure.Config;

namespace AzureFilesSync.Infrastructure.Transfers;

public sealed class AzureFileTransferExecutor : ITransferExecutor
{
    private static readonly HashSet<int> TransientStatuses = [408, 429, 500, 502, 503, 504];
    public const string UnresolvedAskConflictMessage = "Transfer canceled: conflict requires user decision, but Ask policy was unresolved at queue time.";

    private readonly IAuthenticationService _authenticationService;
    private readonly ICheckpointStore _checkpointStore;
    private readonly AzureClientOptions _options;
    private readonly HttpPipelineTransport? _transport;

    public AzureFileTransferExecutor(IAuthenticationService authenticationService, ICheckpointStore checkpointStore, AzureClientOptions options,
        HttpPipelineTransport? transport = null)
    {
        _authenticationService = authenticationService;
        _checkpointStore = checkpointStore;
        _options = options;
        _transport = transport;
    }

    public Task<long> EstimateSizeAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        if (request.Direction == TransferDirection.Upload)
        {
            return Task.FromResult(new FileInfo(request.LocalPath).Length);
        }

        if (request.RemotePath.ProviderKind == RemoteProviderKind.AzureBlob)
        {
            return GetRemoteBlobLengthAsync(request, cancellationToken);
        }

        return GetRemoteLengthAsync(request, cancellationToken);
    }

    public Task CleanupAsync(Guid jobId, TransferRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Direction == TransferDirection.Download)
        {
            ProtectedDownloadFile.Reset(ProtectedDownloadFile.GetStagingPath(request.LocalPath, jobId));
        }
        return Task.CompletedTask;
    }

    public async Task ExecuteAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        if (request.RemotePath.ProviderKind == RemoteProviderKind.AzureBlob)
        {
            await ExecuteBlobTransferAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Direction == TransferDirection.Upload)
        {
            await UploadAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteBlobTransferAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        if (request.Direction == TransferDirection.Upload)
        {
            await UploadBlobAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadBlobAsync(jobId, request, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UploadBlobAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(request.LocalPath);
        var totalBytes = fileInfo.Length;
        var sourceVersion = GetLocalSourceVersion(fileInfo);
        var blockBlobClient = await GetRemoteBlockBlobClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        if (request.ConflictPolicy == TransferConflictPolicy.Ask &&
            await blockBlobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(UnresolvedAskConflictMessage);
        }

        var validCheckpoint = TransferCheckpointPolicy.IsUsable(
            checkpoint,
            request,
            totalBytes,
            sourceVersion,
            stagingPath: null,
            stagingFileExists: false);
        if (!validCheckpoint)
        {
            await _checkpointStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        var chunkSize = ResolveBlobChunkSize(request);
        var ranges = BuildRanges(totalBytes, chunkSize).ToList();
        var completedRanges = RestoreCompletedRanges(checkpoint, totalBytes, chunkSize);
        if (completedRanges.Count > 0)
        {
            var availableBlocks = await GetAvailableUncommittedBlocksAsync(blockBlobClient, cancellationToken).ConfigureAwait(false);
            completedRanges.RemoveWhere(range =>
                !availableBlocks.TryGetValue(BuildBlockId(jobId, range.Offset), out var size) || size != range.Length);
        }

        var completedBytes = completedRanges.Sum(range => (long)range.Length);
        progress(new TransferProgress(completedBytes, totalBytes));
        var throttler = new TransferRateLimiter(ResolveMaxBytesPerSecond(request));
        using var checkpointLock = new SemaphoreSlim(1, 1);
        using var fileHandle = File.OpenHandle(
            request.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = ResolveConcurrency(request)
        };
        var pendingRanges = ranges.Where(range => !completedRanges.Contains(range)).ToList();

        await Parallel.ForEachAsync(pendingRanges, parallelOptions, async (range, ct) =>
        {
            var buffer = new byte[range.Length];
            var readTotal = await ReadRangeAsync(fileHandle, buffer, range, ct).ConfigureAwait(false);
            if (readTotal != range.Length)
            {
                throw new EndOfStreamException($"Expected {range.Length} bytes from the local file but read {readTotal}.");
            }

            await throttler.WaitAsync(readTotal, ct).ConfigureAwait(false);
            var blockId = BuildBlockId(jobId, range.Offset);
            await ExecuteWithRetryAsync(
                async () =>
                {
                    await using var payload = new MemoryStream(buffer, writable: false);
                    await blockBlobClient.StageBlockAsync(blockId, payload, cancellationToken: ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            await checkpointLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                completedRanges.Add(range);
                completedBytes += readTotal;
                progress(new TransferProgress(completedBytes, totalBytes));
                await SaveRangeCheckpointAsync(
                    jobId,
                    request,
                    totalBytes,
                    completedBytes,
                    sourceVersion,
                    stagingPath: null,
                    completedRanges,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                checkpointLock.Release();
            }
        }).ConfigureAwait(false);

        var orderedBlockIds = ranges.Select(range => BuildBlockId(jobId, range.Offset));
        await ExecuteWithRetryAsync(
            () => blockBlobClient.CommitBlockListAsync(orderedBlockIds, cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        progress(new TransferProgress(totalBytes, totalBytes));
    }

    private async Task DownloadBlobAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var blobClient = await GetRemoteBlobClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var properties = await ExecuteWithRetryAsync(
            () => blobClient.GetPropertiesAsync(cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var totalBytes = properties.Value.ContentLength;
        var sourceVersion = properties.Value.ETag.ToString();
        var stagingPath = ProtectedDownloadFile.GetStagingPath(request.LocalPath, jobId);
        var validCheckpoint = TransferCheckpointPolicy.IsUsable(
            checkpoint,
            request,
            totalBytes,
            sourceVersion,
            stagingPath,
            File.Exists(stagingPath));
        if (!validCheckpoint)
        {
            ProtectedDownloadFile.Reset(stagingPath);
            await _checkpointStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        var directory = Path.GetDirectoryName(request.LocalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (request.ConflictPolicy == TransferConflictPolicy.Ask && File.Exists(request.LocalPath))
        {
            throw new InvalidOperationException(UnresolvedAskConflictMessage);
        }

        var existingLength = File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;
        var offset = checkpoint is null
            ? 0
            : Math.Min(Math.Min(checkpoint.NextOffset, existingLength), totalBytes);
        var chunkSize = ResolveBlobChunkSize(request);
        var throttler = new TransferRateLimiter(ResolveMaxBytesPerSecond(request));

        await using (var output = new FileStream(
                         stagingPath,
                         FileMode.OpenOrCreate,
                         FileAccess.Write,
                         FileShare.None,
                         chunkSize,
                         useAsync: true))
        {
            output.SetLength(totalBytes);
            while (offset < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rangeLength = Math.Min(chunkSize, totalBytes - offset);
                var response = await ExecuteWithRetryAsync(
                    () => blobClient.DownloadStreamingAsync(
                        new BlobDownloadOptions
                        {
                            Range = new HttpRange(offset, rangeLength),
                            Conditions = new BlobRequestConditions { IfMatch = properties.Value.ETag }
                        },
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await using var remoteStream = response.Value.Content;
                output.Seek(offset, SeekOrigin.Begin);
                var startPosition = output.Position;
                await remoteStream.CopyToAsync(output, chunkSize, cancellationToken).ConfigureAwait(false);
                var copied = output.Position - startPosition;
                if (copied != rangeLength)
                {
                    throw new EndOfStreamException($"Expected {rangeLength} bytes from the remote blob but received {copied}.");
                }

                await throttler.WaitAsync((int)rangeLength, cancellationToken).ConfigureAwait(false);
                offset += rangeLength;
                progress(new TransferProgress(offset, totalBytes));
                await ProtectedDownloadFile.SaveCheckpointAsync(output, _checkpointStore,
                    new TransferCheckpoint(
                        jobId,
                        request.Direction,
                        request.LocalPath,
                        request.RemotePath,
                        totalBytes,
                        offset,
                        DateTimeOffset.UtcNow,
                        sourceVersion,
                        stagingPath),
                    cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (properties.Value.ContentHash is { Length: > 0 } remoteHash)
        {
            var localHash = ComputeMd5(stagingPath);
            if (!localHash.SequenceEqual(remoteHash))
            {
                throw new InvalidOperationException("Downloaded file hash does not match remote content hash.");
            }
        }

        ProtectedDownloadFile.Commit(stagingPath, request.LocalPath);
    }

    private async Task UploadAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(request.LocalPath);
        var totalBytes = fileInfo.Length;
        var sourceVersion = GetLocalSourceVersion(fileInfo);
        var chunkSize = ResolveFileChunkSize(request);
        var maxConcurrency = ResolveConcurrency(request);
        var maxBytesPerSecond = ResolveMaxBytesPerSecond(request);
        var throttler = new TransferRateLimiter(maxBytesPerSecond);

        var validCheckpoint = TransferCheckpointPolicy.IsUsable(
            checkpoint,
            request,
            totalBytes,
            sourceVersion,
            stagingPath: null,
            stagingFileExists: false);
        var offset = validCheckpoint ? checkpoint!.NextOffset : 0;
        if (!validCheckpoint && checkpoint is not null)
        {
            await _checkpointStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        offset = Math.Clamp(offset, 0, totalBytes);

        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken, ensureRemotePathForUpload: true).ConfigureAwait(false);
        if (request.ConflictPolicy == TransferConflictPolicy.Ask &&
            await fileClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(UnresolvedAskConflictMessage);
        }

        await EnsureRemoteUploadFileAsync(fileClient, totalBytes, forceCreate: offset == 0, cancellationToken).ConfigureAwait(false);

        var hasParallelCheckpoint = checkpoint?.CompletedRanges is { Count: > 0 };

        // A linear checkpoint must resume sequentially. Parallel checkpoints retain completed ranges.
        if (offset > 0 && !hasParallelCheckpoint)
        {
            maxConcurrency = 1;
        }

        if (maxConcurrency <= 1 && !hasParallelCheckpoint)
        {
            await UploadSequentialAsync(jobId, request, fileClient, totalBytes, offset, chunkSize, sourceVersion, throttler, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        await UploadParallelAsync(
            jobId,
            request,
            checkpoint,
            fileClient,
            totalBytes,
            chunkSize,
            sourceVersion,
            maxConcurrency,
            throttler,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadSequentialAsync(
        Guid jobId,
        TransferRequest request,
        ShareFileClient fileClient,
        long totalBytes,
        long startOffset,
        int chunkSize,
        string sourceVersion,
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
            await _checkpointStore.SaveAsync(
                new TransferCheckpoint(
                    jobId,
                    request.Direction,
                    request.LocalPath,
                    request.RemotePath,
                    totalBytes,
                    offset,
                    DateTimeOffset.UtcNow,
                    sourceVersion),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UploadParallelAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        ShareFileClient fileClient,
        long totalBytes,
        int chunkSize,
        string sourceVersion,
        int maxConcurrency,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var ranges = BuildRanges(totalBytes, chunkSize).ToList();
        var completedRanges = RestoreCompletedRanges(checkpoint, totalBytes, chunkSize);
        long completedBytes = completedRanges.Sum(range => (long)range.Length);
        progress(new TransferProgress(completedBytes, totalBytes));
        using var checkpointLock = new SemaphoreSlim(1, 1);
        using var fileHandle = File.OpenHandle(request.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxConcurrency
        };
        var pendingRanges = ranges.Where(range => !completedRanges.Contains(range)).ToList();

        await Parallel.ForEachAsync(pendingRanges, parallelOptions, async (range, ct) =>
        {
            var buffer = new byte[range.Length];
            var readTotal = await ReadRangeAsync(fileHandle, buffer, range, ct).ConfigureAwait(false);
            if (readTotal != range.Length)
            {
                throw new EndOfStreamException($"Expected {range.Length} bytes from the local file but read {readTotal}.");
            }

            await throttler.WaitAsync(readTotal, ct).ConfigureAwait(false);
            await ExecuteWithRetryAsync(
                async () =>
                {
                    await using var payload = new MemoryStream(buffer, 0, readTotal, writable: false);
                    await fileClient.UploadRangeAsync(new HttpRange(range.Offset, readTotal), payload, cancellationToken: ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            await checkpointLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                completedRanges.Add(range);
                completedBytes += readTotal;
                progress(new TransferProgress(completedBytes, totalBytes));
                await SaveRangeCheckpointAsync(
                    jobId,
                    request,
                    totalBytes,
                    completedBytes,
                    sourceVersion,
                    stagingPath: null,
                    completedRanges,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                checkpointLock.Release();
            }
        }).ConfigureAwait(false);
    }

    private async Task DownloadAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var properties = await ExecuteWithRetryAsync(
            () => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var totalBytes = properties.Value.ContentLength;
        var sourceVersion = properties.Value.ETag.ToString();
        var stagingPath = ProtectedDownloadFile.GetStagingPath(request.LocalPath, jobId);
        var chunkSize = ResolveFileChunkSize(request);
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

        var validCheckpoint = TransferCheckpointPolicy.IsUsable(
            checkpoint,
            request,
            totalBytes,
            sourceVersion,
            stagingPath,
            File.Exists(stagingPath));
        if (!validCheckpoint)
        {
            ProtectedDownloadFile.Reset(stagingPath);
            await _checkpointStore.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        var existingLength = File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;
        var checkpointOffset = checkpoint?.NextOffset ?? 0;
        var offset = checkpoint is null ? 0 : Math.Min(Math.Min(checkpointOffset, existingLength), totalBytes);

        var hasParallelCheckpoint = checkpoint?.CompletedRanges is { Count: > 0 };

        // A linear checkpoint must resume sequentially. Parallel checkpoints retain completed ranges.
        if (offset > 0 && !hasParallelCheckpoint)
        {
            maxConcurrency = 1;
        }

        if (maxConcurrency <= 1 && !hasParallelCheckpoint)
        {
            await DownloadSequentialAsync(
                jobId,
                request,
                fileClient,
                totalBytes,
                offset,
                chunkSize,
                sourceVersion,
                stagingPath,
                throttler,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadParallelAsync(
                jobId,
                request,
                checkpoint,
                fileClient,
                totalBytes,
                chunkSize,
                sourceVersion,
                maxConcurrency,
                stagingPath,
                throttler,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        var currentProperties = await ExecuteWithRetryAsync(
            () => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (currentProperties.Value.ETag != properties.Value.ETag)
        {
            throw new InvalidOperationException("Remote file changed while it was being downloaded; the original local file was preserved.");
        }

        if (properties.Value.ContentHash is { Length: > 0 } remoteHash)
        {
            var localHash = ComputeMd5(stagingPath);
            if (!localHash.SequenceEqual(remoteHash))
            {
                throw new InvalidOperationException("Downloaded file hash does not match remote content hash.");
            }
        }

        ProtectedDownloadFile.Commit(stagingPath, request.LocalPath);
    }

    private async Task DownloadSequentialAsync(
        Guid jobId,
        TransferRequest request,
        ShareFileClient fileClient,
        long totalBytes,
        long startOffset,
        int chunkSize,
        string sourceVersion,
        string stagingPath,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var offset = startOffset;
        await using var output = new FileStream(stagingPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, chunkSize, useAsync: true);
        output.SetLength(totalBytes);

        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = Math.Min(chunkSize, totalBytes - offset);
            var response = await ExecuteWithRetryAsync(
                () => fileClient.DownloadAsync(
                    new ShareFileDownloadOptions { Range = new HttpRange(offset, chunkLength) },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await using var remoteStream = response.Value.Content;
            output.Seek(offset, SeekOrigin.Begin);
            var startPosition = output.Position;
            await remoteStream.CopyToAsync(output, chunkSize, cancellationToken).ConfigureAwait(false);
            var copied = output.Position - startPosition;
            if (copied != chunkLength)
            {
                throw new EndOfStreamException($"Expected {chunkLength} bytes from the remote file but received {copied}.");
            }

            var transferred = (int)chunkLength;
            await throttler.WaitAsync(transferred, cancellationToken).ConfigureAwait(false);
            offset += chunkLength;
            progress(new TransferProgress(offset, totalBytes));
            await ProtectedDownloadFile.SaveCheckpointAsync(output, _checkpointStore,
                new TransferCheckpoint(
                    jobId,
                    request.Direction,
                    request.LocalPath,
                    request.RemotePath,
                    totalBytes,
                    offset,
                    DateTimeOffset.UtcNow,
                    sourceVersion,
                    stagingPath),
                cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadParallelAsync(
        Guid jobId,
        TransferRequest request,
        TransferCheckpoint? checkpoint,
        ShareFileClient fileClient,
        long totalBytes,
        int chunkSize,
        string sourceVersion,
        int maxConcurrency,
        string stagingPath,
        TransferRateLimiter throttler,
        Action<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var ranges = BuildRanges(totalBytes, chunkSize).ToList();
        var completedRanges = RestoreCompletedRanges(checkpoint, totalBytes, chunkSize);
        long completedBytes = completedRanges.Sum(range => (long)range.Length);
        progress(new TransferProgress(completedBytes, totalBytes));
        using var checkpointLock = new SemaphoreSlim(1, 1);
        using var outputHandle = File.OpenHandle(stagingPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RandomAccess.SetLength(outputHandle, totalBytes);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxConcurrency
        };
        var pendingRanges = ranges.Where(range => !completedRanges.Contains(range)).ToList();

        await Parallel.ForEachAsync(pendingRanges, parallelOptions, async (range, ct) =>
        {
            await throttler.WaitAsync(range.Length, ct).ConfigureAwait(false);
            var response = await ExecuteWithRetryAsync(
                () => fileClient.DownloadAsync(
                    new ShareFileDownloadOptions { Range = new HttpRange(range.Offset, range.Length) },
                    ct),
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

            if (copied != range.Length)
            {
                throw new EndOfStreamException($"Expected {range.Length} bytes from the remote file but received {copied}.");
            }

            await RandomAccess.WriteAsync(outputHandle, buffer.AsMemory(0, copied), range.Offset, ct).ConfigureAwait(false);
            await checkpointLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                completedRanges.Add(range);
                completedBytes += copied;
                progress(new TransferProgress(completedBytes, totalBytes));
                await SaveRangeCheckpointAsync(
                    jobId,
                    request,
                    totalBytes,
                    completedBytes,
                    sourceVersion,
                    stagingPath,
                    completedRanges,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                checkpointLock.Release();
            }
        }).ConfigureAwait(false);
    }

    private async Task<long> GetRemoteLengthAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var props = await ExecuteWithRetryAsync(() => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        return props.Value.ContentLength;
    }

    private async Task<long> GetRemoteBlobLengthAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        var blobClient = await GetRemoteBlobClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var props = await ExecuteWithRetryAsync(() => blobClient.GetPropertiesAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        return props.Value.ContentLength;
    }

    private async Task<ShareFileClient> GetRemoteFileClientAsync(SharePath path, CancellationToken cancellationToken, bool ensureRemotePathForUpload = false)
    {
        var clientOptions = new ShareClientOptions { ShareTokenIntent = ShareTokenIntent.Backup };
        if (_transport is not null)
        {
            clientOptions.Transport = _transport;
        }
        var serviceClient = new ShareServiceClient(
            new Uri($"https://{path.StorageAccountName}.file.core.windows.net"),
            _authenticationService.GetCredential(),
            clientOptions);
        var shareClient = serviceClient.GetShareClient(path.ShareName);

        var normalized = path.NormalizeRelativePath();
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        ShareDirectoryClient directoryClient = shareClient.GetRootDirectoryClient();
        foreach (var segment in segments.Take(Math.Max(0, segments.Length - 1)))
        {
            directoryClient = directoryClient.GetSubdirectoryClient(segment);
            if (ensureRemotePathForUpload)
            {
                await ExecuteWithRetryAsync(() => directoryClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
            }
        }

        var fileName = segments.LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Remote file path is missing a file name.");
        }

        return directoryClient.GetFileClient(fileName);
    }

    private Task<BlobClient> GetRemoteBlobClientAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serviceClient = new BlobServiceClient(
            new Uri($"https://{path.StorageAccountName}.blob.core.windows.net"),
            _authenticationService.GetCredential(), GetBlobClientOptions());
        var containerClient = serviceClient.GetBlobContainerClient(path.ShareName);
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Remote file path is missing a file name.");
        }

        return Task.FromResult(containerClient.GetBlobClient(normalized));
    }

    private Task<BlockBlobClient> GetRemoteBlockBlobClientAsync(SharePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serviceClient = new BlobServiceClient(
            new Uri($"https://{path.StorageAccountName}.blob.core.windows.net"),
            _authenticationService.GetCredential(), GetBlobClientOptions());
        var containerClient = serviceClient.GetBlobContainerClient(path.ShareName);
        var normalized = path.NormalizeRelativePath();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Remote file path is missing a file name.");
        }

        return Task.FromResult(containerClient.GetBlockBlobClient(normalized));
    }

    private BlobClientOptions GetBlobClientOptions()
    {
        var options = new BlobClientOptions();
        if (_transport is not null)
        {
            options.Transport = _transport;
        }
        return options;
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

    private async Task SaveRangeCheckpointAsync(
        Guid jobId,
        TransferRequest request,
        long totalBytes,
        long completedBytes,
        string sourceVersion,
        string? stagingPath,
        HashSet<TransferRange> completedRanges,
        CancellationToken cancellationToken)
    {
        var ranges = completedRanges
            .OrderBy(range => range.Offset)
            .Select(range => new TransferRangeCheckpoint(range.Offset, range.Length))
            .ToList();
        await _checkpointStore.SaveAsync(
            new TransferCheckpoint(
                jobId,
                request.Direction,
                request.LocalPath,
                request.RemotePath,
                totalBytes,
                completedBytes,
                DateTimeOffset.UtcNow,
                sourceVersion,
                stagingPath,
                ranges),
            cancellationToken).ConfigureAwait(false);
    }

    private static HashSet<TransferRange> RestoreCompletedRanges(
        TransferCheckpoint? checkpoint,
        long totalBytes,
        int chunkSize)
    {
        return TransferCheckpointPolicy.GetUsableCompletedRanges(checkpoint, totalBytes, chunkSize)
            .Select(range => new TransferRange(range.Offset, range.Length))
            .ToHashSet();
    }

    private static async Task<int> ReadRangeAsync(
        SafeFileHandle fileHandle,
        byte[] buffer,
        TransferRange range,
        CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < range.Length)
        {
            var read = await RandomAccess.ReadAsync(
                fileHandle,
                buffer.AsMemory(readTotal, range.Length - readTotal),
                range.Offset + readTotal,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            readTotal += read;
        }

        return readTotal;
    }

    private static string BuildBlockId(Guid jobId, long offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{jobId:N}:{offset:D20}"));

    private static async Task<Dictionary<string, long>> GetAvailableUncommittedBlocksAsync(
        BlockBlobClient blockBlobClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await blockBlobClient.GetBlockListAsync(
                BlockListTypes.Uncommitted,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Value.UncommittedBlocks.ToDictionary(block => block.Name, block => block.SizeLong);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return [];
        }
    }

    private static byte[] ComputeMd5(string path)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return md5.ComputeHash(stream);
    }

    private static string GetLocalSourceVersion(FileInfo fileInfo) =>
        $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";

    private int ResolveFileChunkSize(TransferRequest request)
    {
        var requested = request.ChunkSizeBytes > 0 ? request.ChunkSizeBytes : _options.TransferChunkSizeBytes;
        return Math.Clamp(requested, 64 * 1024, 4 * 1024 * 1024);
    }

    private int ResolveBlobChunkSize(TransferRequest request)
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

