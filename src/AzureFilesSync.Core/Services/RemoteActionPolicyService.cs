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

        if (capability.State != RemoteAccessState.Accessible)
        {
            var reason = string.IsNullOrWhiteSpace(capability.UserMessage)
                ? "Remote side is not accessible."
                : capability.UserMessage;
            return new RemoteActionPolicy(false, false, false, false, reason);
        }

        if (inputs.IsMirrorPlanning)
        {
            return new RemoteActionPolicy(false, false, false, false, "Mirror planning is currently running.");
        }

        return new RemoteActionPolicy(
            CanEnqueueUpload: inputs.HasSelectedLocalFile,
            CanEnqueueDownload: inputs.HasSelectedRemoteFile,
            CanPlanMirror: true,
            CanExecuteMirror: inputs.HasMirrorPlan);
    }
}
