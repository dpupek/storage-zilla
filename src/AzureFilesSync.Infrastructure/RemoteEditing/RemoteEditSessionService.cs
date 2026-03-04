using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;

namespace AzureFilesSync.Infrastructure.RemoteEditing;

public sealed class RemoteEditSessionService : IRemoteEditSessionService, IDisposable
{
    private readonly ITransferExecutor _transferExecutor;
    private readonly IAzureFilesBrowserService _azureFilesBrowserService;
    private readonly ILocalFileOperationsService _localFileOperationsService;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, SessionState> _sessions = [];
    private readonly string _tempRoot;

    public RemoteEditSessionService(
        ITransferExecutor transferExecutor,
        IAzureFilesBrowserService azureFilesBrowserService,
        ILocalFileOperationsService localFileOperationsService)
    {
        _transferExecutor = transferExecutor;
        _azureFilesBrowserService = azureFilesBrowserService;
        _localFileOperationsService = localFileOperationsService;
        _tempRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzureFilesSync",
            "remote-edit");
    }

    public async Task<RemoteEditOpenResult> OpenAsync(SharePath remotePath, string displayName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var remoteEntry = await _azureFilesBrowserService.GetEntryDetailsAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (remoteEntry is null || remoteEntry.IsDirectory)
        {
            throw new InvalidOperationException($"Remote file '{remotePath.NormalizeRelativePath()}' was not found.");
        }

        var localPath = BuildTempLocalPath(remotePath, displayName);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var request = new TransferRequest(
            TransferDirection.Download,
            localPath,
            remotePath,
            ConflictPolicy: TransferConflictPolicy.Overwrite,
            MaxConcurrency: 1);

        await _transferExecutor.ExecuteAsync(
            Guid.NewGuid(),
            request,
            checkpoint: null,
            _ => { },
            cancellationToken).ConfigureAwait(false);

        var localFingerprint = LocalFingerprint.FromPath(localPath);
        var remoteFingerprint = RemoteFingerprint.FromEntry(remoteEntry);
        var sessionId = Guid.NewGuid();
        var watcher = CreateWatcher(localPath, () => MarkDirty(sessionId));
        var session = new SessionState(
            sessionId,
            displayName,
            remotePath,
            localPath,
            DateTimeOffset.UtcNow,
            localFingerprint,
            remoteFingerprint,
            watcher);

        try
        {
            await _localFileOperationsService.OpenAsync(localPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CleanupSession(session);
            throw;
        }

        lock (_sync)
        {
            _sessions[sessionId] = session;
        }

        return new RemoteEditOpenResult(sessionId, localPath);
    }

    public async Task<IReadOnlyList<RemoteEditPendingChange>> GetPendingChangesAsync(CancellationToken cancellationToken)
    {
        var sessions = SnapshotSessions();
        var results = new List<RemoteEditPendingChange>(sessions.Count);

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localChanged = ReconcileLocalChange(session);
            if (!localChanged)
            {
                continue;
            }

            var remoteChanged = await HasRemoteChangedAsync(session, cancellationToken).ConfigureAwait(false);
            results.Add(new RemoteEditPendingChange(
                session.SessionId,
                session.DisplayName,
                session.RemotePath.NormalizeRelativePath(),
                session.LocalPath,
                LocalChanged: true,
                RemoteChanged: remoteChanged,
                session.OpenedUtc));
        }

        return results;
    }

    public async Task<RemoteEditSyncResult> SyncAsync(Guid sessionId, bool overwriteIfRemoteChanged, CancellationToken cancellationToken)
    {
        if (!TryGetSession(sessionId, out var session))
        {
            return new RemoteEditSyncResult(sessionId, RemoteEditSyncOutcome.SessionNotFound, "Remote edit session no longer exists.");
        }

        if (!ReconcileLocalChange(session))
        {
            return new RemoteEditSyncResult(sessionId, RemoteEditSyncOutcome.NoLocalChanges, "Local file has no pending changes.");
        }

        var remoteChanged = await HasRemoteChangedAsync(session, cancellationToken).ConfigureAwait(false);
        if (remoteChanged && !overwriteIfRemoteChanged)
        {
            return new RemoteEditSyncResult(sessionId, RemoteEditSyncOutcome.RemoteChangedNeedsConfirmation, "Remote file changed since open.");
        }

        var request = new TransferRequest(
            TransferDirection.Upload,
            session.LocalPath,
            session.RemotePath,
            ConflictPolicy: TransferConflictPolicy.Overwrite,
            MaxConcurrency: 1);

        await _transferExecutor.ExecuteAsync(
            Guid.NewGuid(),
            request,
            checkpoint: null,
            _ => { },
            cancellationToken).ConfigureAwait(false);

        await RemoveAndCleanupSessionAsync(sessionId).ConfigureAwait(false);
        return new RemoteEditSyncResult(sessionId, RemoteEditSyncOutcome.Synced);
    }

    public async Task<bool> DiscardAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RemoveAndCleanupSessionAsync(sessionId).ConfigureAwait(false);
    }

    public void Dispose()
    {
        List<SessionState> sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            CleanupSession(session);
        }
    }

    private List<SessionState> SnapshotSessions()
    {
        lock (_sync)
        {
            return [.. _sessions.Values];
        }
    }

    private bool TryGetSession(Guid sessionId, out SessionState session)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out session!);
        }
    }

    private void MarkDirty(Guid sessionId)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                _sessions[sessionId] = session with { Dirty = true };
            }
        }
    }

    private bool ReconcileLocalChange(SessionState session)
    {
        var localChanged = HasLocalChanged(session);
        if (!localChanged && session.Dirty)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(session.SessionId, out var tracked))
                {
                    _sessions[session.SessionId] = tracked with { Dirty = false };
                }
            }
        }

        return localChanged;
    }

    private async Task<bool> RemoveAndCleanupSessionAsync(Guid sessionId)
    {
        SessionState? removed = null;
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                removed = session;
                _sessions.Remove(sessionId);
            }
        }

        if (removed is null)
        {
            return false;
        }

        await Task.Run(() => CleanupSession(removed), CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static void CleanupSession(SessionState session)
    {
        try
        {
            session.Watcher.Dispose();
        }
        catch
        {
            // Best effort cleanup.
        }

        try
        {
            if (File.Exists(session.LocalPath))
            {
                File.Delete(session.LocalPath);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static FileSystemWatcher CreateWatcher(string localPath, Action onDirty)
    {
        var directory = Path.GetDirectoryName(localPath)!;
        var filename = Path.GetFileName(localPath);
        var watcher = new FileSystemWatcher(directory, filename)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler dirtyHandler = (_, _) => onDirty();
        RenamedEventHandler renameHandler = (_, _) => onDirty();
        ErrorEventHandler errorHandler = (_, _) => onDirty();
        watcher.Changed += dirtyHandler;
        watcher.Created += dirtyHandler;
        watcher.Deleted += dirtyHandler;
        watcher.Renamed += renameHandler;
        watcher.Error += errorHandler;

        return watcher;
    }

    private bool HasLocalChanged(SessionState session)
    {
        var current = LocalFingerprint.FromPath(session.LocalPath);
        return current != session.BaselineLocal;
    }

    private async Task<bool> HasRemoteChangedAsync(SessionState session, CancellationToken cancellationToken)
    {
        var remoteEntry = await _azureFilesBrowserService.GetEntryDetailsAsync(session.RemotePath, cancellationToken).ConfigureAwait(false);
        if (remoteEntry is null || remoteEntry.IsDirectory)
        {
            return true;
        }

        var current = RemoteFingerprint.FromEntry(remoteEntry);
        return current != session.BaselineRemote;
    }

    private string BuildTempLocalPath(SharePath remotePath, string displayName)
    {
        var safeFileName = SanitizeFileName(displayName);
        var storage = SanitizePathSegment(remotePath.StorageAccountName);
        var share = SanitizePathSegment(remotePath.ShareName);
        var folder = Path.Combine(_tempRoot, storage, share);
        Directory.CreateDirectory(folder);

        return Path.Combine(folder, $"{Guid.NewGuid():N}_{safeFileName}");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "remote" : sanitized;
    }

    private static string SanitizeFileName(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "remote-file" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "remote-file" : sanitized;
    }

    private readonly record struct LocalFingerprint(bool Exists, long Length, DateTime LastWriteUtc)
    {
        public static LocalFingerprint FromPath(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new LocalFingerprint(false, 0, DateTime.MinValue);
            }

            return new LocalFingerprint(true, info.Length, info.LastWriteTimeUtc);
        }
    }

    private readonly record struct RemoteFingerprint(long Length, DateTimeOffset? LastWriteTime)
    {
        public static RemoteFingerprint FromEntry(RemoteEntry entry) =>
            new(entry.Length, entry.LastWriteTime);
    }

    private sealed record SessionState(
        Guid SessionId,
        string DisplayName,
        SharePath RemotePath,
        string LocalPath,
        DateTimeOffset OpenedUtc,
        LocalFingerprint BaselineLocal,
        RemoteFingerprint BaselineRemote,
        FileSystemWatcher Watcher,
        bool Dirty = false);
}
