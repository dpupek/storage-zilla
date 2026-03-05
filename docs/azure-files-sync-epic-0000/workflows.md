# Workflows

## Persona: Developer
Motivation: Use Azure Files like an FTP client without scripting.

## Flow 1: Sign In and Discover
1. Open app and click Sign In.
2. Complete interactive login (WAM broker when available, otherwise system browser fallback).
3. Select subscription, storage account, and remote root (file share or blob container) using labeled selectors.
4. Verify status bar shows signed-in identity and current transfer tuning (throttle/concurrency).
5. Provider selection is inferred automatically from root kind; no manual provider toggle is required.
6. If a saved endpoint is unavailable (for example DNS/network issue), keep the session signed in and show a recoverable selector-level message while allowing account/root reselection.
7. Legacy profiles without stored root kind restore to Azure File Share by default when root names overlap between file shares and blob containers.

Linked cases: epic-0000, child-0001, child-0010

## Flow 2: Upload/Download Queue
1. Browse local folder on left pane.
2. Browse Azure remote root on right pane (Azure Files or Azure Blob) with a consistent hierarchical file/folder UX.
3. Select an entry and keep selection stable while metadata enriches.
4. Queue upload/download from selected entry.
5. If file conflict exists, apply default conflict policy (`Ask/Skip/Overwrite/Rename`); `Ask` can apply `Do for all` for current selected batch.
6. Observe queue status and progress; filter by status/direction when triaging.
7. Re-queuing the same active transfer is prevented to avoid accidental duplicate work.
8. A single `Run Queue` action drains queued work without repeated clicks.
9. For blob roots, non-existent virtual-folder paths are treated as `NotFound` and recover to a valid prior/root path instead of rendering a blank grid.

Linked cases: child-0002, child-0010

## Flow 3: Mirror Sync with Delete Guard
1. Mirror controls are temporarily hidden while mirror-specific conflict UX is being designed.
2. Existing mirror service contracts remain in code for future re-enable.

Linked cases: child-0003

## Flow 4: In-App Help and User Documentation
1. Open `Help -> Help` from the main menu.
2. Embedded help window opens to `Overview`.
3. Navigate guide sections from the left topic list or inline links.
4. View workflow instructions, queue operations, control reference, and troubleshooting without leaving the app.

Linked cases: child-0007

## Flow 5: Open, Edit, and Sync a Remote File
1. In the remote pane, open a remote file from context menu or double-click.
2. App downloads the file to a managed temp location and opens it with the system-associated app.
3. User edits locally; app tracks candidate changes via watcher events and validates by local fingerprint before treating as pending.
4. When app regains focus, if a real local change exists, prompt user to upload, defer, or discard local temp copy.
5. If remote changed since session start, require explicit overwrite confirmation before upload.
6. After upload or discard, clean up watcher/session/temp file immediately.

Linked cases: child-0011
