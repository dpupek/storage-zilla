# Developer Docs

This section is for contributors, maintainers, and release engineers.

## Current UI Snapshot
![Storage Zilla App Screenshot](../../img/screen.png)

## Project Layout
- `src/AzureFilesSync.Core`: core contracts/models/services.
- `src/AzureFilesSync.Infrastructure`: Azure auth/discovery/storage integration and update services.
- `src/AzureFilesSync.Desktop`: WPF desktop UI and view models.
- `tests/AzureFilesSync.Tests`: unit tests.
- `tests/AzureFilesSync.IntegrationTests`: integration tests.
- `tests/AzureFilesSync.UITests`: UI/viewmodel tests.

## Requirements
- .NET SDK 10.x
- Windows (`net10.0-windows` for desktop)

## Build, Test, Run
Build:
```powershell
dotnet build AzureFilesSync.slnx -c Debug
```

Test:
```powershell
dotnet test AzureFilesSync.slnx -c Debug
```

Run desktop:
```powershell
dotnet run --project src/AzureFilesSync.Desktop/AzureFilesSync.Desktop.csproj -c Debug
```

## Installer and Release Pipeline
- Packaging project: `installer/StorageZilla.Package/StorageZilla.Package.wapproj`
- Release workflow: `.github/workflows/release-msix.yml`
- Release tags: `v1.x.x` (example: `v1.4.2`)
- Version consistency:
  - Tag `v1.4.2` maps to app version `1.4.2`
  - About dialog shows aligned version
  - MSIX package version uses four-part format (`1.4.2.0`)

Create release tag:
```powershell
git tag v1.4.2
git push origin v1.4.2
```

### GitHub Secrets for Release
- `MSIX_CERT_BASE64`: base64 PFX certificate
- `MSIX_CERT_PASSWORD`: PFX password
- `MSIX_PUBLISHER`: publisher subject (example: `CN=Danm@de Software`)

## Auto Update (Technical)
- Triggered manually from Help/About.
- Checks latest stable GitHub release (`dpupek/storage-zilla`).
- Verifies:
  - SHA256 from `SHA256SUMS.txt`
  - MSIX publisher: `CN=Danm@de Software`
  - package version matches release version
- On success, prompts user and launches MSIX installer.

## Logging
- Serilog file sink is enabled.
- Current desktop default level: `Debug`.
- Logs write under local app data.

## Quick Memory MCP
Endpoint: `storage-zilla`

Typical sequence:
1. `coldStart(endpoint: "storage-zilla")`
2. `searchEntries(endpoint: "storage-zilla", request: { text: "<topic>" })`
3. `upsertEntry(endpoint: "storage-zilla", entry: { ... })`
