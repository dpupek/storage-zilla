using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Files.Shares;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;
using AzureFilesSync.Infrastructure.Transfers;

namespace AzureFilesSync.IntegrationTests;

public sealed class LiveAzureFilesIntegrationTests
{
    [Fact]
    public async Task Live_UploadDownloadAndResume_VerifiesIntegrity_WhenConfigured()
    {
        #region Arrange
        var cfg = LiveConfig.FromEnvironment();
        if (!cfg.Enabled)
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "AzureFilesSyncLive", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var sourcePath = Path.Combine(tempRoot, "source.bin");
        var downloadedPath = Path.Combine(tempRoot, "downloaded.bin");
        var resumedPath = Path.Combine(tempRoot, "resumed.bin");

        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(sourcePath, payload);

        var relative = string.IsNullOrWhiteSpace(cfg.Prefix)
            ? $"live-tests/{Guid.NewGuid():N}/payload.bin"
            : $"{cfg.Prefix.Trim('/')}/live-tests/{Guid.NewGuid():N}/payload.bin";

        var remotePath = new SharePath(cfg.StorageAccount, cfg.Share, relative);
        var checkpointStore = new InMemoryCheckpointStore();
        var executor = new AzureFileTransferExecutor(
            new StaticCredentialAuthenticationService(new DefaultAzureCredential()),
            checkpointStore,
            new AzureClientOptions
            {
                TransferChunkSizeBytes = 64 * 1024,
                TransferRetryAttempts = 3,
                TransferRetryBaseDelayMs = 200
            });

        var uploadJobId = Guid.NewGuid();
        var downloadJobId = Guid.NewGuid();
        var resumeJobId = Guid.NewGuid();
        #endregion

        #region Initial Assert
        Assert.True(File.Exists(sourcePath));
        #endregion

        #region Act
        await executor.ExecuteAsync(
            uploadJobId,
            new TransferRequest(TransferDirection.Upload, sourcePath, remotePath),
            checkpoint: null,
            progress: _ => { },
            CancellationToken.None);

        await executor.ExecuteAsync(
            downloadJobId,
            new TransferRequest(TransferDirection.Download, downloadedPath, remotePath),
            checkpoint: null,
            progress: _ => { },
            CancellationToken.None);

        var half = payload.Length / 2;
        await File.WriteAllBytesAsync(resumedPath, payload.Take(half).ToArray());
        await checkpointStore.SaveAsync(
            new TransferCheckpoint(
                resumeJobId,
                TransferDirection.Download,
                resumedPath,
                remotePath,
                payload.Length,
                half,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var resumeCheckpoint = await checkpointStore.LoadAsync(resumeJobId, CancellationToken.None);
        await executor.ExecuteAsync(
            resumeJobId,
            new TransferRequest(TransferDirection.Download, resumedPath, remotePath),
            resumeCheckpoint,
            progress: _ => { },
            CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal(HashFile(sourcePath), HashFile(downloadedPath));
        Assert.Equal(HashFile(sourcePath), HashFile(resumedPath));
        #endregion

        await DeleteRemoteFileIfExistsAsync(cfg, relative);
        Directory.Delete(tempRoot, recursive: true);
    }

    private static async Task DeleteRemoteFileIfExistsAsync(LiveConfig cfg, string relativePath)
    {
        var serviceClient = new ShareServiceClient(new Uri($"https://{cfg.StorageAccount}.file.core.windows.net"), new DefaultAzureCredential());
        var shareClient = serviceClient.GetShareClient(cfg.Share);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Last();
        var directorySegments = segments.Take(segments.Length - 1).ToList();

        var directoryClient = shareClient.GetRootDirectoryClient();
        foreach (var segment in directorySegments)
        {
            directoryClient = directoryClient.GetSubdirectoryClient(segment);
        }

        await directoryClient.GetFileClient(fileName).DeleteIfExistsAsync();
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private sealed record LiveConfig(bool Enabled, string StorageAccount, string Share, string Prefix)
    {
        public static LiveConfig FromEnvironment()
        {
            var enabled = string.Equals(Environment.GetEnvironmentVariable("AFS_LIVE_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
            var account = Environment.GetEnvironmentVariable("AFS_LIVE_STORAGE_ACCOUNT") ?? string.Empty;
            var share = Environment.GetEnvironmentVariable("AFS_LIVE_SHARE") ?? string.Empty;
            var prefix = Environment.GetEnvironmentVariable("AFS_LIVE_PREFIX") ?? string.Empty;

            return new LiveConfig(enabled && !string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(share), account, share, prefix);
        }
    }

    private sealed class StaticCredentialAuthenticationService : IAuthenticationService
    {
        private readonly TokenCredential _credential;

        public StaticCredentialAuthenticationService(TokenCredential credential)
        {
            _credential = credential;
        }

        public Task<LoginSession> SignInInteractiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LoginSession(true, "live", "tenant"));

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public TokenCredential GetCredential() => _credential;
    }

    private sealed class InMemoryCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<Guid, TransferCheckpoint> _checkpoints = [];

        public Task<TransferCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
        {
            _checkpoints.TryGetValue(jobId, out var checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            _checkpoints[checkpoint.JobId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
        {
            _checkpoints.Remove(jobId);
            return Task.CompletedTask;
        }
    }
}
