using System.Text.Json;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Local;

public sealed class FileConnectionProfileStore : IConnectionProfileStore
{
    private readonly string _profilePath;

    public FileConnectionProfileStore(string? rootPath = null)
    {
        var root = rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzureFilesSync");
        Directory.CreateDirectory(root);
        _profilePath = Path.Combine(root, "connection-profile.json");
    }

    public async Task<ConnectionProfile> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_profilePath))
        {
            return ConnectionProfile.Empty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        await using var stream = File.OpenRead(_profilePath);
        var profile = await JsonSerializer.DeserializeAsync<ConnectionProfile>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return profile ?? ConnectionProfile.Empty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_profilePath);
        await JsonSerializer.SerializeAsync(stream, profile, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
