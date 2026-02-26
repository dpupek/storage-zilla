# Workflows

## Persona: Developer
Motivation: Use Azure Files like an FTP client without scripting.

## Flow 1: Sign In and Discover
1. Open app and click Sign In.
2. Complete interactive browser login.
3. Select subscription, storage account, and file share using labeled selectors.
4. Verify status bar shows signed-in identity and current transfer tuning (throttle/concurrency).

Linked cases: epic-0000, child-0001

## Flow 2: Upload/Download Queue
1. Browse local folder on left pane.
2. Browse Azure folder on right pane.
3. Select an entry and keep selection stable while metadata enriches.
4. Queue upload/download from selected entry.
5. If file conflict exists, apply default conflict policy (`Ask/Skip/Overwrite/Rename`); `Ask` can apply `Do for all` for current selected batch.
6. Observe queue status and progress; filter by status/direction when triaging.
7. Re-queuing the same active transfer is prevented to avoid accidental duplicate work.
8. A single `Run Queue` action drains queued work without repeated clicks.

Linked cases: child-0002

## Flow 3: Mirror Sync with Delete Guard
1. Mirror controls are temporarily hidden while mirror-specific conflict UX is being designed.
2. Existing mirror service contracts remain in code for future re-enable.

Linked cases: child-0003
