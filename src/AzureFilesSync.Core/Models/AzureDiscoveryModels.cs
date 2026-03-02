namespace AzureFilesSync.Core.Models;

public enum RemoteProviderKind
{
    AzureFiles,
    AzureBlob
}

public enum RemoteRootKind
{
    FileShare,
    BlobContainer
}

public sealed record SubscriptionItem(string Id, string Name);
public sealed record StorageAccountItem(string SubscriptionId, string Name, string ResourceGroup, string? Kind = null);
public sealed record FileShareItem(string Name, RemoteRootKind Kind = RemoteRootKind.FileShare)
{
    public RemoteProviderKind ProviderKind => Kind == RemoteRootKind.BlobContainer
        ? RemoteProviderKind.AzureBlob
        : RemoteProviderKind.AzureFiles;

    public string DisplayName => Kind == RemoteRootKind.BlobContainer
        ? $"{Name} (Blob Container)"
        : $"{Name} (File Share)";
}
