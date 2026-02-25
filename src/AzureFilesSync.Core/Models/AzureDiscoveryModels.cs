namespace AzureFilesSync.Core.Models;

public sealed record SubscriptionItem(string Id, string Name);
public sealed record StorageAccountItem(string SubscriptionId, string Name, string ResourceGroup);
public sealed record FileShareItem(string Name);
