using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using System.Net.Http;
using System.Net.Sockets;

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
            yield return new StorageAccountItem(
                subscriptionId,
                account.Data.Name,
                account.Data.Id.ResourceGroupName ?? string.Empty,
                account.Data.Kind.ToString());
        }
    }

    public async IAsyncEnumerable<FileShareItem> ListFileSharesAsync(
        string storageAccountName,
        bool includeFileShares,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var roots = new List<FileShareItem>();
        var failures = new List<Exception>();

        if (includeFileShares)
        {
            try
            {
                var fileServiceUri = new Uri($"https://{storageAccountName}.file.core.windows.net");
                var fileServiceClient = new ShareServiceClient(
                    fileServiceUri,
                    _authenticationService.GetCredential(),
                    new ShareClientOptions
                    {
                        ShareTokenIntent = ShareTokenIntent.Backup
                    });

                await foreach (var item in fileServiceClient.GetSharesAsync(cancellationToken: cancellationToken))
                {
                    roots.Add(new FileShareItem(item.Name, RemoteRootKind.FileShare));
                }
            }
            catch (Exception ex) when (CanIgnoreFileEndpointFailure(ex))
            {
                failures.Add(ex);
            }
        }

        try
        {
            var blobServiceUri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
            var blobServiceClient = new BlobServiceClient(blobServiceUri, _authenticationService.GetCredential());
            await foreach (var item in blobServiceClient.GetBlobContainersAsync(cancellationToken: cancellationToken))
            {
                roots.Add(new FileShareItem(item.Name, RemoteRootKind.BlobContainer));
            }
        }
        catch (Exception ex) when (CanIgnoreBlobEndpointFailure(ex))
        {
            failures.Add(ex);
        }

        if (roots.Count == 0 && failures.Count > 0)
        {
            throw failures[0];
        }

        foreach (var root in roots
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Kind))
        {
            yield return root;
        }
    }

    private static bool CanIgnoreFileEndpointFailure(Exception exception) => CanIgnoreEndpointFailure(exception);

    private static bool CanIgnoreBlobEndpointFailure(Exception exception) => CanIgnoreEndpointFailure(exception);

    private static bool CanIgnoreEndpointFailure(Exception exception)
    {
        return exception switch
        {
            SocketException socket => socket.SocketErrorCode == SocketError.HostNotFound,
            HttpRequestException http => http.InnerException is not null && CanIgnoreEndpointFailure(http.InnerException),
            RequestFailedException request => request.Status is 0 or 400 or 403 or 404 ||
                                              (request.InnerException is not null && CanIgnoreEndpointFailure(request.InnerException)) ||
                                              request.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase),
            AggregateException aggregate => aggregate.Flatten().InnerExceptions.All(CanIgnoreEndpointFailure),
            _ when exception.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) => true,
            _ when exception.InnerException is not null && CanIgnoreEndpointFailure(exception.InnerException) => true,
            _ => false
        };
    }
}
