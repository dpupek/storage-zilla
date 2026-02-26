using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Config;

public sealed class UpdateOptions
{
    public string Owner { get; set; } = "dpupek";
    public string Repo { get; set; } = "storage-zilla";
    public string ExpectedPublisher { get; set; } = "CN=Danm@de Software";
    public string ReleaseAssetExtension { get; set; } = ".msix";
    public string Sha256FileName { get; set; } = "SHA256SUMS.txt";
    public string UserAgent { get; set; } = "StorageZilla-Desktop-Updater";
    public UpdateChannel DefaultChannel { get; set; } = UpdateChannel.Stable;
    public bool AllowBetaChannel { get; set; } = true;
}
