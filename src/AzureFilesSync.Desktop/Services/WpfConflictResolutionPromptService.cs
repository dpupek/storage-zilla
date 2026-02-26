using System.Windows;
using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Dialogs;

namespace AzureFilesSync.Desktop.Services;

public sealed class WpfConflictResolutionPromptService : IConflictResolutionPromptService
{
    public bool TryResolveConflict(
        TransferDirection direction,
        string sourcePath,
        string destinationPath,
        out ConflictPromptAction action,
        out bool doForAll)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            action = ConflictPromptAction.CancelBatch;
            doForAll = false;
            return false;
        }

        return ConflictResolutionWindow.TryShow(
            owner,
            direction,
            sourcePath,
            destinationPath,
            out action,
            out doForAll);
    }
}
