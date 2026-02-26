# Storage Zilla

![Storage Zilla Logo](img/storage-zilla-logo-icon.png)

Storage Zilla is a .NET 10 WPF desktop client that provides an FTP-style, side-by-side workflow for Azure File Shares:
- Interactive Azure sign-in
- Subscription and storage account discovery
- File share browsing
- Queue-based upload/download
- Mirror planning and execution
- Capability-aware UX (permission/help states in the remote pane)

## Solution Layout
- `src/AzureFilesSync.Core`: core models, interfaces, policy/services
- `src/AzureFilesSync.Infrastructure`: Azure auth/discovery/files APIs, queue plumbing
- `src/AzureFilesSync.Desktop`: WPF UI, `MainViewModel`, dialogs, logging
- `tests/AzureFilesSync.Tests`: unit tests
- `tests/AzureFilesSync.IntegrationTests`: integration tests
- `tests/AzureFilesSync.UITests`: UI/viewmodel tests

## Requirements
- .NET SDK 10.x
- Windows (WPF target: `net10.0-windows`)
- Azure identity with appropriate Azure Files data permissions when using OAuth/REST

## Build
```powershell
dotnet build AzureFilesSync.slnx -c Debug
```

## Test
```powershell
dotnet test AzureFilesSync.slnx -c Debug
```

## Run
```powershell
dotnet run --project src/AzureFilesSync.Desktop/AzureFilesSync.Desktop.csproj -c Debug
```

## Installer (MSIX)
- Packaging project: `installer/StorageZilla.Package/StorageZilla.Package.wapproj`
- Release architecture: `x64`
- Runtime model: self-contained (`win-x64`) so installer includes .NET runtime dependencies.

## Release Tags and Version Consistency
- Release automation triggers on tags that match `v1.x.x` (example: `v1.4.2`).
- The tag version (`1.4.2`) is applied to:
  - assembly informational version
  - file/assembly versions
  - About dialog version text
- MSIX package version is set to `1.4.2.0` (Windows package format requires four parts).

Create and push a release tag:
```powershell
git tag v1.4.2
git push origin v1.4.2
```

## GitHub Release Pipeline Secrets
The release workflow `.github/workflows/release-msix.yml` requires:
- `MSIX_CERT_BASE64`: base64-encoded PFX certificate content.
- `MSIX_CERT_PASSWORD`: PFX password.
- `MSIX_PUBLISHER`: publisher subject string (must match certificate subject), for example `CN=Danm@de Software`.

## Licensing
Storage Zilla is dual-licensed:
- `GPL-3.0-or-later` (open-source use and distribution under GPL terms), or
- a separate commercial license.

Choose one license path:
1. Use under GPL terms: see [LICENSE](LICENSE).
2. Use under commercial terms: see [LICENSE-COMMERCIAL](LICENSE-COMMERCIAL).

## Logging
- App uses Serilog with file sink.
- Current default log level is debug for troubleshooting.
- Logs are written to local filesystem by the desktop app configuration.

## Azure Permissions Notes
If sign-in succeeds but remote directory listing fails with `AuthorizationPermissionMismatch`, grant one of the Azure Files privileged data roles (for example `Storage File Data Privileged Reader` or `Storage File Data Privileged Contributor`) on the target storage account (or broader scope if intended).

The app surfaces this as a non-blocking informational message in the remote pane instead of a modal error where possible.

## Quick Memory MCP
This project uses Quick Memory endpoint `storage-zilla` for project memory.

Typical sequence:
1. `coldStart(endpoint: "storage-zilla")`
2. `searchEntries(endpoint: "storage-zilla", request: { text: "<topic>" })`
3. `upsertEntry(endpoint: "storage-zilla", entry: { ... })`
