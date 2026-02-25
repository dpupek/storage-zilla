using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class RemoteCapabilityService : IRemoteCapabilityService
{
    private readonly IAzureFilesBrowserService _azureFilesBrowserService;
    private readonly IRemoteErrorInterpreter _errorInterpreter;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, RemoteCapabilitySnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public RemoteCapabilityService(IAzureFilesBrowserService azureFilesBrowserService, IRemoteErrorInterpreter errorInterpreter)
    {
        _azureFilesBrowserService = azureFilesBrowserService;
        _errorInterpreter = errorInterpreter;
    }

    public async Task<RemoteCapabilitySnapshot> EvaluateAsync(RemoteContext context, CancellationToken cancellationToken)
    {
        if (!context.IsValid)
        {
            return RemoteCapabilitySnapshot.InvalidSelection("Select a valid storage account and file share.");
        }

        var key = BuildKey(context);
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var existing) && DateTimeOffset.UtcNow - existing.EvaluatedUtc <= _cacheTtl)
            {
                return existing;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        return await RefreshAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteCapabilitySnapshot> RefreshAsync(RemoteContext context, CancellationToken cancellationToken)
    {
        RemoteCapabilitySnapshot snapshot;

        if (!context.IsValid)
        {
            snapshot = RemoteCapabilitySnapshot.InvalidSelection("Select a valid storage account and file share.");
        }
        else
        {
            try
            {
                var sharePath = new SharePath(context.StorageAccountName, context.ShareName, context.Path);
                await _azureFilesBrowserService.ListDirectoryAsync(sharePath, cancellationToken).ConfigureAwait(false);
                snapshot = RemoteCapabilitySnapshot.Accessible();
            }
            catch (Exception ex)
            {
                snapshot = _errorInterpreter.Interpret(ex, context);
            }
        }

        var key = BuildKey(context);
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache[key] = snapshot;
        }
        finally
        {
            _cacheLock.Release();
        }

        return snapshot;
    }

    public RemoteCapabilitySnapshot? GetLastKnown(RemoteContext context)
    {
        var key = BuildKey(context);
        return _cache.TryGetValue(key, out var snapshot) ? snapshot : null;
    }

    private static string BuildKey(RemoteContext context) =>
        $"{context.SubscriptionId ?? string.Empty}|{context.StorageAccountName}|{context.ShareName}|{context.Path}";
}
