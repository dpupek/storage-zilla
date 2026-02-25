# Workflows

## Persona: Developer
Motivation: Use Azure Files like an FTP client without scripting.

## Flow 1: Sign In and Discover
1. Open app and click Sign In.
2. Complete interactive browser login.
3. Select subscription.
4. Select storage account.
5. Select file share.

Linked cases: epic-0000, child-0001

## Flow 2: Upload/Download Queue
1. Browse local folder on left pane.
2. Browse Azure folder on right pane.
3. Queue upload/download from selected entry.
4. Observe queue status and progress.

Linked cases: child-0002

## Flow 3: Mirror Sync with Delete Guard
1. Select local root and remote root.
2. Build mirror plan.
3. Review create/update/delete counts.
4. Confirm delete warning.
5. Queue mirror operations.

Linked cases: child-0003
