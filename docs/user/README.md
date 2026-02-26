# User Docs

This section is for end users running Storage Zilla.

## App Screenshot
![Storage Zilla App Screenshot](../../img/screen.png)

## What Storage Zilla Does
Storage Zilla gives you an FTP-style dual-pane desktop experience for Azure File Shares:
- Browse local files and Azure file shares side-by-side
- Queue uploads and downloads
- Manage transfer conflicts with explicit policies
- Use safe, guided update checks from the app

## Getting Started
1. Launch Storage Zilla.
2. Sign in using interactive Azure authentication.
3. Select Subscription, Storage Account, and File Share.
4. Browse local and remote paths.
5. Queue or start transfers from context menu actions.

## Permissions
If sign-in works but remote browse fails with `AuthorizationPermissionMismatch`, your identity needs Azure Files data permissions on the target scope.

Common roles:
- `Storage File Data Privileged Reader`
- `Storage File Data Privileged Contributor`

Storage Zilla shows permission issues as an informational message in the remote pane instead of blocking with modal errors where possible.

## Auto Update
- Open `Help -> Check for Updates...` (or use the button in About).
- Choose update channel in `Tools -> Settings`:
  - `Stable` (default): production releases from `main`
  - `Beta`: prerelease builds from `beta`
- Storage Zilla checks the latest release in your selected channel.
- If an update is available and validated, you will be prompted to install.

## Support and Licensing
- Open source use: `GPL-3.0-or-later` ([LICENSE](../../LICENSE))
- Commercial terms: [LICENSE-COMMERCIAL](../../LICENSE-COMMERCIAL)
