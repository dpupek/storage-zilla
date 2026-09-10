using System.Text.Json;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Transfers;

public sealed class FileTransferJobStore : ITransferJobStore
{
    private readonly Lock _sync = new();
    private readonly string _root;

    public FileTransferJobStore(string? rootPath = null)
    {
        _root = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzureFilesSync",
            "transfer-jobs");
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<TransferJobSnapshot> Load()
    {
        lock (_sync)
        {
            var snapshots = new List<TransferJobSnapshot>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var snapshot = JsonSerializer.Deserialize<TransferJobSnapshot>(json);
                    if (snapshot is not null)
                    {
                        snapshots.Add(snapshot);
                    }
                }
                catch (JsonException)
                {
                    // An invalid job record is isolated to its file; valid jobs remain recoverable.
                }
                catch (IOException)
                {
                    // A concurrently inaccessible record must not prevent app startup.
                }
            }

            return snapshots;
        }
    }

    public void Save(TransferJobSnapshot snapshot)
    {
        lock (_sync)
        {
            var path = GetPath(snapshot.JobId);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    public void Delete(Guid jobId)
    {
        lock (_sync)
        {
            var path = GetPath(jobId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string GetPath(Guid jobId) => Path.Combine(_root, $"{jobId:N}.json");
}
