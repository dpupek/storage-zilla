using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Config;
using AzureFilesSync.Infrastructure.Transfers;
using Xunit.Abstractions;

namespace AzureFilesSync.IntegrationTests;

public sealed class LiveAzureStorageIntegrationTests(ITestOutputHelper output)
{
    [LiveAzureFact]
    public async Task Live_UploadDownloadAndResume_VerifiesIntegrity_WhenConfigured()
    {
        #region Arrange
        var cfg = LiveConfig.FromEnvironment();
        var tempRoot = Path.Combine(FindRepositoryRoot(), ".tmp", "live-azure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var sourcePath = Path.Combine(tempRoot, "source.bin");
        var downloadedPath = Path.Combine(tempRoot, "downloaded.bin");
        var resumedPath = Path.Combine(tempRoot, "resumed.bin");

        var payload = new byte[512 * 1024 + 17];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(sourcePath, payload);

        var relative = string.IsNullOrWhiteSpace(cfg.Prefix)
            ? $"storage-zilla-live-{Guid.NewGuid():N}/payload.bin"
            : $"{cfg.Prefix.Trim('/')}/storage-zilla-live-{Guid.NewGuid():N}/payload.bin";

        var remotePath = new SharePath(cfg.StorageAccount, cfg.Share, relative, cfg.Provider);
        output.WriteLine($"Live target: {cfg.Provider} {cfg.StorageAccount}/{cfg.Share}/{relative}");
        var checkpointRoot = Path.Combine(tempRoot, "checkpoints");
        var checkpointStore = new FileCheckpointStore(checkpointRoot);
        var executor = new AzureFileTransferExecutor(
            new StaticCredentialAuthenticationService(new AzureCliCredential()),
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

        Exception? testFailure = null;
        try
        {
            #region Act
            await executor.ExecuteAsync(
                uploadJobId,
                new TransferRequest(TransferDirection.Upload, sourcePath, remotePath, ChunkSizeBytes: 64 * 1024),
                checkpoint: null,
                progress: _ => { },
                CancellationToken.None);

            await executor.ExecuteAsync(
                downloadJobId,
                new TransferRequest(TransferDirection.Download, downloadedPath, remotePath, ChunkSizeBytes: 64 * 1024),
                checkpoint: null,
                progress: _ => { },
                CancellationToken.None);

            await File.WriteAllTextAsync(resumedPath, "existing destination");
            var resumeRequest = new TransferRequest(TransferDirection.Download, resumedPath, remotePath,
                TransferConflictPolicy.Overwrite, ChunkSizeBytes: 64 * 1024);
            using var interruption = new CancellationTokenSource();
            var interruptedExecutor = new AzureFileTransferExecutor(
                new StaticCredentialAuthenticationService(new AzureCliCredential()),
                new InterruptingCheckpointStore(checkpointStore, interruption), new AzureClientOptions());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interruptedExecutor.ExecuteAsync(
                resumeJobId, resumeRequest, null, _ => { }, interruption.Token));
            Assert.Equal("existing destination", await File.ReadAllTextAsync(resumedPath));

            // Re-open persisted state, just as a new application instance would.
            var restoredStore = new FileCheckpointStore(checkpointRoot);
            var resumeCheckpoint = await restoredStore.LoadAsync(resumeJobId, CancellationToken.None);
            Assert.NotNull(resumeCheckpoint);
            Assert.InRange(resumeCheckpoint.NextOffset, 1, payload.Length - 1);
            if (cfg.Provider == RemoteProviderKind.AzureFiles)
            {
                Assert.NotEmpty(resumeCheckpoint.CompletedRanges!);
            }
            var restoredExecutor = new AzureFileTransferExecutor(
                new StaticCredentialAuthenticationService(new AzureCliCredential()), restoredStore, new AzureClientOptions());
            await restoredExecutor.ExecuteAsync(
                resumeJobId,
                resumeRequest,
                resumeCheckpoint,
                progress: _ => { },
                CancellationToken.None);
            #endregion

            #region Assert
            Assert.Equal(HashFile(sourcePath), HashFile(downloadedPath));
            Assert.Equal(HashFile(sourcePath), HashFile(resumedPath));
            Assert.False(File.Exists(ProtectedDownloadFile.GetStagingPath(resumedPath, resumeJobId)));
            #endregion

        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                await DeleteRemoteFileIfExistsAsync(cfg, relative);
            }
            catch (Exception cleanupFailure) when (testFailure is not null)
            {
                throw new AggregateException("Live test and remote cleanup both failed.", testFailure, cleanupFailure);
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [LiveAzureTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_InterruptedUpload_ResumesOrRejectsChangedSource(bool changeSource)
    {
        // Arrange
        var cfg = LiveConfig.FromEnvironment();
        var runId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.Combine(FindRepositoryRoot(), ".tmp", "live-azure-" + runId);
        Directory.CreateDirectory(tempRoot);
        var source = Path.Combine(tempRoot, "source.bin");
        var downloaded = Path.Combine(tempRoot, "downloaded.bin");
        var payload = RandomNumberGenerator.GetBytes(512 * 1024 + 17);
        await File.WriteAllBytesAsync(source, payload);
        var relative = $"{cfg.Prefix.Trim('/')}/storage-zilla-live-{runId}/payload.bin".TrimStart('/');
        output.WriteLine($"Live target: {cfg.Provider} {cfg.StorageAccount}/{cfg.Share}/{relative}");
        var request = new TransferRequest(TransferDirection.Upload, source,
            new SharePath(cfg.StorageAccount, cfg.Share, relative, cfg.Provider), TransferConflictPolicy.Overwrite,
            ChunkSizeBytes: 64 * 1024);
        var jobId = Guid.NewGuid();
        var checkpointRoot = Path.Combine(tempRoot, "checkpoints");
        var store = new FileCheckpointStore(checkpointRoot);
        using var interruption = new CancellationTokenSource();
        var auth = new StaticCredentialAuthenticationService(new AzureCliCredential());
        var executor = new AzureFileTransferExecutor(auth, new InterruptingCheckpointStore(store, interruption), new AzureClientOptions());

        // Initial Assert
        Assert.Null(await store.LoadAsync(jobId, CancellationToken.None));
        Exception? testFailure = null;
        try
        {
            Assert.False(cfg.Provider == RemoteProviderKind.AzureBlob
                ? (await BuildRemoteBlobClient(cfg, relative).ExistsAsync()).Value
                : (await BuildRemoteFileClient(cfg, relative).ExistsAsync()).Value);
            // Act: interrupt only after a real remote range and its checkpoint have been saved.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
                jobId, request, null, _ => { }, interruption.Token));
            var restoredStore = new FileCheckpointStore(checkpointRoot);
            var checkpoint = await restoredStore.LoadAsync(jobId, CancellationToken.None);
            Assert.NotNull(checkpoint);
            Assert.InRange(checkpoint.NextOffset, 1, payload.Length - 1);
            Assert.NotEmpty(checkpoint.CompletedRanges!);
            if (changeSource)
            {
                await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(payload.Length));
                File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(1));
            }
            var restoredExecutor = new AzureFileTransferExecutor(auth, restoredStore, new AzureClientOptions());
            await restoredExecutor.ExecuteAsync(jobId, request, checkpoint, _ => { }, CancellationToken.None);
            await restoredExecutor.ExecuteAsync(Guid.NewGuid(), new TransferRequest(TransferDirection.Download,
                downloaded, request.RemotePath, ChunkSizeBytes: 64 * 1024), null, _ => { }, CancellationToken.None);

            // Assert
            Assert.Equal(HashFile(source), HashFile(downloaded));
            Assert.Equal(payload.Length, new FileInfo(downloaded).Length);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try { await DeleteRemoteFileIfExistsAsync(cfg, relative); }
            catch (Exception cleanupFailure) when (testFailure is not null)
            {
                throw new AggregateException("Live test and remote cleanup both failed.", testFailure, cleanupFailure);
            }
            finally { Directory.Delete(tempRoot, recursive: true); }
        }
    }

    private static async Task DeleteRemoteFileIfExistsAsync(LiveConfig cfg, string relativePath)
    {
        if (cfg.Provider == RemoteProviderKind.AzureBlob)
        {
            await BuildRemoteBlobClient(cfg, relativePath).DeleteIfExistsAsync();
            return;
        }
        await BuildRemoteFileClient(cfg, relativePath).DeleteIfExistsAsync();
        await BuildRemoteDirectoryClient(cfg, relativePath).DeleteIfExistsAsync();
    }

    private static BlobClient BuildRemoteBlobClient(LiveConfig cfg, string relativePath) =>
        new BlobServiceClient(new Uri($"https://{cfg.StorageAccount}.blob.core.windows.net"), new AzureCliCredential())
            .GetBlobContainerClient(cfg.Share).GetBlobClient(relativePath);

    private static ShareFileClient BuildRemoteFileClient(LiveConfig cfg, string relativePath)
    {
        return BuildRemoteDirectoryClient(cfg, relativePath).GetFileClient(relativePath.Split('/').Last());
    }

    private static ShareDirectoryClient BuildRemoteDirectoryClient(LiveConfig cfg, string relativePath)
    {
        var serviceClient = new ShareServiceClient(new Uri($"https://{cfg.StorageAccount}.file.core.windows.net"),
            new AzureCliCredential(), new ShareClientOptions { ShareTokenIntent = ShareTokenIntent.Backup });
        var shareClient = serviceClient.GetShareClient(cfg.Share);

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directorySegments = segments.Take(segments.Length - 1).ToList();

        var directoryClient = shareClient.GetRootDirectoryClient();
        foreach (var segment in directorySegments)
        {
            directoryClient = directoryClient.GetSubdirectoryClient(segment);
        }

        return directoryClient;
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private sealed record LiveConfig(bool Enabled, string StorageAccount, string Share, string Prefix, RemoteProviderKind Provider)
    {
        public static LiveConfig FromEnvironment()
        {
            var enabled = string.Equals(Environment.GetEnvironmentVariable("AFS_LIVE_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
            var account = Environment.GetEnvironmentVariable("AFS_LIVE_STORAGE_ACCOUNT") ?? string.Empty;
            var share = Environment.GetEnvironmentVariable("AFS_LIVE_SHARE") ?? string.Empty;
            var prefix = Environment.GetEnvironmentVariable("AFS_LIVE_PREFIX") ?? string.Empty;

            if (!enabled || string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(share))
            {
                throw new InvalidOperationException("Live tests require AFS_LIVE_ENABLED=true, AFS_LIVE_STORAGE_ACCOUNT, and AFS_LIVE_SHARE.");
            }
            var providerName = Environment.GetEnvironmentVariable("AFS_LIVE_PROVIDER") ?? nameof(RemoteProviderKind.AzureFiles);
            if (!Enum.TryParse<RemoteProviderKind>(providerName, out var provider) || !Enum.IsDefined(provider))
            {
                throw new InvalidOperationException("AFS_LIVE_PROVIDER must be AzureFiles or AzureBlob.");
            }
            return new LiveConfig(enabled, account, share, prefix, provider);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AzureFilesSync.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Run live tests from the Storage Zilla repository.");
    }

    public sealed class LiveAzureFactAttribute : FactAttribute
    {
        public LiveAzureFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("AFS_LIVE_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Live Azure test disabled: set AFS_LIVE_ENABLED=true and configure a test account/share.";
            }
        }
    }

    public sealed class LiveAzureTheoryAttribute : TheoryAttribute
    {
        public LiveAzureTheoryAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("AFS_LIVE_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Live Azure test disabled: set AFS_LIVE_ENABLED=true and configure a test account/share.";
            }
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

    private sealed class InterruptingCheckpointStore(ICheckpointStore inner, CancellationTokenSource interruption) : ICheckpointStore
    {
        public Task<TransferCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken) => inner.LoadAsync(jobId, cancellationToken);
        public async Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            await inner.SaveAsync(checkpoint, cancellationToken);
            interruption.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken) => inner.DeleteAsync(jobId, cancellationToken);
    }
}
