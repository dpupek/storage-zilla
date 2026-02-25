using System.Security.Cryptography;
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

        var offset = checkpoint?.NextOffset ?? 0;
        if (checkpoint is not null && checkpoint.TotalBytes != totalBytes)
        {
            offset = 0;
        }

        offset = Math.Clamp(offset, 0, totalBytes);

        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        await EnsureRemoteUploadFileAsync(fileClient, totalBytes, forceCreate: offset == 0, cancellationToken).ConfigureAwait(false);

        await using var input = new FileStream(request.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, _options.TransferChunkSizeBytes, useAsync: true);
        input.Seek(offset, SeekOrigin.Begin);

        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Math.Min(_options.TransferChunkSizeBytes, totalBytes - offset);
            var buffer = new byte[chunk];
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)chunk), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

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

    private async Task DownloadAsync(Guid jobId, TransferRequest request, TransferCheckpoint? checkpoint, Action<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var fileClient = await GetRemoteFileClientAsync(request.RemotePath, cancellationToken).ConfigureAwait(false);
        var properties = await ExecuteWithRetryAsync(
            () => fileClient.GetPropertiesAsync(cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var totalBytes = properties.Value.ContentLength;

        var directory = Path.GetDirectoryName(request.LocalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existingLength = File.Exists(request.LocalPath) ? new FileInfo(request.LocalPath).Length : 0;
        var checkpointOffset = checkpoint?.NextOffset ?? 0;
        var offset = checkpoint is null ? 0 : Math.Min(Math.Min(checkpointOffset, existingLength), totalBytes);

        await using var output = new FileStream(request.LocalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, _options.TransferChunkSizeBytes, useAsync: true);
        output.SetLength(totalBytes);

        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Math.Min(_options.TransferChunkSizeBytes, totalBytes - offset);
            var response = await ExecuteWithRetryAsync(
                () => fileClient.DownloadAsync(new ShareFileDownloadOptions { Range = new HttpRange(offset, chunk) }, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await using var remoteStream = response.Value.Content;
            output.Seek(offset, SeekOrigin.Begin);
            await remoteStream.CopyToAsync(output, _options.TransferChunkSizeBytes, cancellationToken).ConfigureAwait(false);

            offset += chunk;
            progress(new TransferProgress(offset, totalBytes));
            await _checkpointStore.SaveAsync(new TransferCheckpoint(jobId, request.Direction, request.LocalPath, request.RemotePath, totalBytes, offset, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        if (properties.Value.ContentHash is { Length: > 0 } remoteHash)
        {
            output.Flush(true);
            var localHash = ComputeMd5(request.LocalPath);
            if (!localHash.SequenceEqual(remoteHash))
            {
                throw new InvalidOperationException("Downloaded file hash does not match remote content hash.");
            }
        }
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
}
