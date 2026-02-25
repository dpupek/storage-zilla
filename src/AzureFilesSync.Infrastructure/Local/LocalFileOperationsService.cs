using AzureFilesSync.Core.Contracts;
using System.Diagnostics;

namespace AzureFilesSync.Infrastructure.Local;

public sealed class LocalFileOperationsService : ILocalFileOperationsService
{
    public Task ShowInExplorerAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        if (Directory.Exists(path) || File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }

        return Task.CompletedTask;
    }

    public Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public Task OpenWithAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"")
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newName))
        {
            return Task.CompletedTask;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var target = Path.Combine(directory, newName);
        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else if (File.Exists(path))
        {
            File.Move(path, target);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
