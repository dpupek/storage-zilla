using System.Text.Json;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Transfers;

public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly string _root;

    public FileCheckpointStore(string? rootPath = null)
    {
        _root = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureFilesSync", "checkpoints");
        Directory.CreateDirectory(_root);
    }

    public async Task<TransferCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var path = GetPath(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TransferCheckpoint>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(TransferCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var path = GetPath(checkpoint.JobId);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, checkpoint, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var path = GetPath(jobId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(Guid jobId) => Path.Combine(_root, $"{jobId:N}.json");
}
