using Azure;
using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

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
}
