using Azure;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using System.Net.Http;
using System.Net.Sockets;

namespace AzureFilesSync.Infrastructure.Azure;

public sealed class RemoteErrorInterpreter : IRemoteErrorInterpreter
{
    private static readonly HashSet<int> TransientStatusCodes = [408, 429, 500, 502, 503, 504];

    public RemoteCapabilitySnapshot Interpret(Exception exception, RemoteContext context)
    {
        if (!context.IsValid)
        {
            return RemoteCapabilitySnapshot.InvalidSelection("Select a valid storage account and file share.");
        }

        if (TryMapEndpointFailure(exception, context, out var endpointFailure))
        {
            return endpointFailure;
        }

        if (exception is RequestFailedException requestFailed)
        {
            if (requestFailed.Status == 403 && string.Equals(requestFailed.ErrorCode, "AuthorizationPermissionMismatch", StringComparison.OrdinalIgnoreCase))
            {
                var message =
                    $"No Azure Files data permission for '{context.StorageAccountName}'. " +
                    "Ask an admin to assign this identity 'Storage File Data Privileged Reader' (browse/read) " +
                    "or 'Storage File Data Privileged Contributor' (read/write) on this storage account.";

                return new RemoteCapabilitySnapshot(
                    RemoteAccessState.PermissionDenied,
                    false,
                    false,
                    false,
                    false,
                    false,
                    message,
                    DateTimeOffset.UtcNow,
                    requestFailed.ErrorCode,
                    requestFailed.Status);
            }

            if (requestFailed.Status == 404)
            {
                return new RemoteCapabilitySnapshot(
                    RemoteAccessState.NotFound,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "The selected remote path or share was not found.",
                    DateTimeOffset.UtcNow,
                    requestFailed.ErrorCode,
                    requestFailed.Status);
            }

            if (TransientStatusCodes.Contains(requestFailed.Status))
            {
                return new RemoteCapabilitySnapshot(
                    RemoteAccessState.TransientFailure,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "Remote service is temporarily unavailable. Try refreshing again.",
                    DateTimeOffset.UtcNow,
                    requestFailed.ErrorCode,
                    requestFailed.Status);
            }

            return new RemoteCapabilitySnapshot(
                RemoteAccessState.Unknown,
                false,
                false,
                false,
                false,
                false,
                $"Remote access failed (HTTP {requestFailed.Status}).",
                DateTimeOffset.UtcNow,
                requestFailed.ErrorCode,
                requestFailed.Status);
        }

        return new RemoteCapabilitySnapshot(
            RemoteAccessState.Unknown,
            false,
            false,
            false,
            false,
            false,
            "Unexpected remote access error.",
            DateTimeOffset.UtcNow);
    }

    private static bool TryMapEndpointFailure(Exception exception, RemoteContext context, out RemoteCapabilitySnapshot snapshot)
    {
        if (!ContainsHostNotFoundFailure(exception))
        {
            snapshot = null!;
            return false;
        }

        var endpointHost = $"{context.StorageAccountName}.file.core.windows.net";
        var message =
            $"Cannot reach Azure Files endpoint '{endpointHost}'. " +
            "Check DNS resolution, firewall/antivirus/proxy allowlists for '*.file.core.windows.net', " +
            "or private endpoint DNS configuration for this storage account.";

        snapshot = new RemoteCapabilitySnapshot(
            RemoteAccessState.EndpointUnavailable,
            false,
            false,
            false,
            false,
            false,
            message,
            DateTimeOffset.UtcNow,
            "DnsHostNotFound");

        return true;
    }

    private static bool ContainsHostNotFoundFailure(Exception exception)
    {
        foreach (var current in EnumerateExceptions(exception))
        {
            if (current is SocketException socketException && socketException.SocketErrorCode == SocketError.HostNotFound)
            {
                return true;
            }

            if (current is HttpRequestException httpRequestException &&
                httpRequestException.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        var stack = new Stack<Exception>();
        stack.Push(exception);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (inner is not null)
                    {
                        stack.Push(inner);
                    }
                }
            }
            else if (current.InnerException is not null)
            {
                stack.Push(current.InnerException);
            }
        }
    }
}
