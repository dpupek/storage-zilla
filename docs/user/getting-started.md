# Getting Started

## Prerequisites
- Windows desktop environment
- Access to at least one Azure subscription
- Azure data-plane permissions on the target storage account or remote root

Recommended roles for Azure File Shares:
- `Storage File Data Privileged Reader`
- `Storage File Data Privileged Contributor`

Recommended roles for Blob Containers:
- `Storage Blob Data Reader`
- `Storage Blob Data Contributor`

## First Run
1. Launch Storage Zilla.
2. Select `File -> Sign In`.
3. Complete interactive Azure login.
4. Choose a `Subscription`, `Storage Account`, and `Remote Root`.
5. Remote root entries are labeled as either `(... File Share)` or `(... Blob Container)`.
6. Browse local and remote paths.

## First Transfer
1. Select a local file in the left grid.
2. Click `>>` or use local context menu `Upload Selection`.
3. Open the `Transfer Queue` panel and verify status changes.
4. Select a remote file and click `<<` for download.

## First Remote Search
1. In the `Remote` pane, enter text in `Search remote...`.
2. Choose scope:
   - `Current Path`
   - `Share Root`
3. Click search.
4. Watch live progress in the remote pane status bar.
5. Use cancel to stop a long-running search, or clear to return to browse mode.

## Settings You Should Review
Open `Tools -> Settings` and configure:
- Max concurrency
- Throttle (KB/s)
- Default upload conflict policy
- Default download conflict policy
- Update channel (`Stable` or `Beta`)

## Profile Behavior
- `File -> Save Profile` stores current paths, selected remote root kind, and settings.
- Profile settings are restored on next launch when possible.
