using AzureFilesSync.Core.Contracts;
using System.Net;
using System.Net.Sockets;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class StorageEndpointPreflightService : IStorageEndpointPreflightService
{
    private const int TimeoutMs = 2500;

    public async Task<(bool Success, string? FailureSummary)> ValidateAsync(string endpointHost, CancellationToken cancellationToken)
    {
        try
        {
            var lookupTask = Dns.GetHostAddressesAsync(endpointHost);
            var timeoutTask = Task.Delay(TimeoutMs, cancellationToken);
            var completedTask = await Task.WhenAny(lookupTask, timeoutTask).ConfigureAwait(false);
            if (completedTask != lookupTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (false, $"DNS lookup timed out after {TimeoutMs} ms.");
            }

            var addresses = await lookupTask.ConfigureAwait(false);
            return addresses.Length == 0
                ? (false, "DNS lookup returned no addresses.")
                : (true, null);
        }
        catch (SocketException ex)
        {
            return (false, $"DNS lookup failed: {ex.SocketErrorCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"DNS lookup timed out after {TimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            return (false, $"Endpoint preflight error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
