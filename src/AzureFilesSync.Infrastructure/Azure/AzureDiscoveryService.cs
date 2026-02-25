using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class AzureDiscoveryService : IAzureDiscoveryService
{
    private readonly IAuthenticationService _authenticationService;

    public AzureDiscoveryService(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    public async IAsyncEnumerable<SubscriptionItem> ListSubscriptionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = new ArmClient(_authenticationService.GetCredential());
        await foreach (var subscription in client.GetSubscriptions().GetAllAsync(cancellationToken: cancellationToken))
        {
            yield return new SubscriptionItem(subscription.Data.SubscriptionId, subscription.Data.DisplayName);
        }
    }

    public async IAsyncEnumerable<StorageAccountItem> ListStorageAccountsAsync(string subscriptionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = new ArmClient(_authenticationService.GetCredential());
        SubscriptionResource? targetSubscription = null;
        await foreach (var subscription in client.GetSubscriptions().GetAllAsync(cancellationToken: cancellationToken))
        {
            if (string.Equals(subscription.Data.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
            {
                targetSubscription = subscription;
                break;
            }
        }

        if (targetSubscription is null)
        {
            yield break;
        }

        foreach (StorageAccountResource account in targetSubscription.GetStorageAccounts())
        {
            yield return new StorageAccountItem(subscriptionId, account.Data.Name, account.Data.Id.ResourceGroupName ?? string.Empty);
        }
    }

    public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(string storageAccountName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var serviceUri = new Uri($"https://{storageAccountName}.file.core.windows.net");
        var serviceClient = new ShareServiceClient(
            serviceUri,
            _authenticationService.GetCredential(),
            new ShareClientOptions
            {
                ShareTokenIntent = ShareTokenIntent.Backup
            });
        await foreach (var item in serviceClient.GetSharesAsync(cancellationToken: cancellationToken))
        {
            yield return new FileShareItem(item.Name);
        }
    }
}
