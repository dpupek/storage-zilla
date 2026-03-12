using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Core.Services;

public sealed class RemoteActionPolicyService : IRemoteActionPolicyService
{
    public RemoteActionPolicy Compute(RemoteCapabilitySnapshot? capability, RemoteActionInputs inputs)
    {
        if (capability is null)
        {
            return new RemoteActionPolicy(false, false, false, false, "Remote capability not evaluated yet.");
        }

        if (inputs.IsMirrorPlanning)
        {
            return new RemoteActionPolicy(false, false, false, false, "Mirror planning is currently running.");
        }

        var canDoAnything =
            capability.CanUpload ||
            capability.CanDownload ||
            capability.CanPlanMirror ||
            capability.CanExecuteMirror;

        if (!canDoAnything)
        {
            var reason = string.IsNullOrWhiteSpace(capability.UserMessage)
                ? "Remote side is not accessible."
                : capability.UserMessage;
            return new RemoteActionPolicy(false, false, false, false, reason);
        }

        return new RemoteActionPolicy(
            CanEnqueueUpload: capability.CanUpload && inputs.HasSelectedLocalFile,
            CanEnqueueDownload: capability.CanDownload && inputs.HasSelectedRemoteFile,
            CanPlanMirror: capability.CanPlanMirror,
            CanExecuteMirror: capability.CanExecuteMirror && inputs.HasMirrorPlan,
            DisableReason: string.IsNullOrWhiteSpace(capability.UserMessage) ? null : capability.UserMessage);
    }
}
