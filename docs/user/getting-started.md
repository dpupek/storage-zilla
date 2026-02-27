# Getting Started

## Prerequisites
- Windows desktop environment
- Access to at least one Azure subscription
- Azure Files data-plane permissions on the target storage account or share

Recommended roles:
- `Storage File Data Privileged Reader`
- `Storage File Data Privileged Contributor`

## First Run
1. Launch Storage Zilla.
2. Select `File -> Sign In`.
3. Complete interactive Azure login.
4. Choose a `Subscription`, `Storage Account`, and `File Share`.
5. Browse local and remote paths.

## First Transfer
1. Select a local file in the left grid.
2. Click `>>` or use local context menu `Upload Selection`.
3. Open the `Transfer Queue` panel and verify status changes.
4. Select a remote file and click `<<` for download.

## Settings You Should Review
Open `Tools -> Settings` and configure:
- Max concurrency
- Throttle (KB/s)
- Default upload conflict policy
- Default download conflict policy
- Update channel (`Stable` or `Beta`)

## Profile Behavior
- `File -> Save Profile` stores current paths and selections.
- Profile settings are restored on next launch when possible.
