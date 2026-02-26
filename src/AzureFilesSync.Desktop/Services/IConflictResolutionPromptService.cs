using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Dialogs;

namespace AzureFilesSync.Desktop.Services;

public interface IConflictResolutionPromptService
{
    bool TryResolveConflict(
        TransferDirection direction,
        string sourcePath,
        string destinationPath,
        out ConflictPromptAction action,
        out bool doForAll);
}
