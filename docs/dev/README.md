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
- Beta workflow: `.github/workflows/release-beta.yml` (`push` to `beta`)
- Prod workflow: `.github/workflows/release-prod.yml` (`push` to `main`)
- Shared reusable workflow: `.github/workflows/release-msix-common.yml` (build/sign/package/release steps)
- Branch flow: `dev` -> `beta` -> `main`
  - `dev`: shared integration, no release publishing
  - `beta`: auto-publishes GitHub prereleases + signed MSIX
- `main`: auto-publishes GitHub stable releases + signed MSIX
- Versioning:
  - Managed by Nerdbank.GitVersioning (`version.json`)
  - Pipeline computes versions automatically from Git history
  - About dialog remains aligned with assembly informational version
  - MSIX package version uses four-part format (`x.y.z.0`)
  - Also publishes an unsigned `x64` MSI artifact for internal/manual install scenarios.
  - Also publishes a portable `win-x64` ZIP package with self-contained binaries.
  - Also publishes a public signing certificate (`StorageZilla-Signing-PublicKey.cer`) and `RELEASE-NOTES.md`.

### Trigger a Beta Release
1. Merge/promote changes from `dev` to `beta`.
2. Push `beta`:
```powershell
git switch beta
git push -u origin beta   # first push only
# later pushes:
# git push origin beta
```
3. Verify run in GitHub Actions: `Release MSIX Beta`.

### Trigger a Production Release
1. Merge/promote validated changes from `beta` to `main`.
2. Push `main`:
```powershell
git switch main
git push origin main
```
3. Verify run in GitHub Actions: `Release MSIX Prod`.

### GitHub Secrets for Release
- `MSIX_CERT_BASE64`: base64 PFX certificate
- `MSIX_CERT_PASSWORD`: PFX password
- `MSIX_PUBLISHER`: publisher subject (example: `CN=Danm@de Software`)

If release fails at `Validate signing certificate` with an empty password or certificate-open error, rotate secrets from the same PFX export:
```powershell
pwsh ./tools/set-msix-secrets.ps1 -PfxPath "C:\path\to\storage-zilla-signing.pfx" -Repo "dpupek/storage-zilla" -Publisher "CN=Danm@de Software"
```
Then re-run beta release:
```powershell
git push origin beta
```

## Auto Update (Technical)
- Triggered manually from Help/About.
- User-selectable channel in Settings: `Stable` (default) or `Beta`.
- Checks latest matching GitHub release (`dpupek/storage-zilla`) by channel.
- Verifies:
  - SHA256 from `SHA256SUMS.txt`
  - MSIX publisher: `CN=Danm@de Software`
  - package version matches release version
- On success, prompts user and launches MSIX installer.

## MSI Artifact
- Release workflow also builds an unsigned MSI (`StorageZilla_<version>_x64_unsigned.msi`) using WiX.
- MSI is attached to the GitHub release for manual/internal installation.
- The MSI is intentionally unsigned and will show Windows trust warnings.

## Portable ZIP Artifact
- Release workflow also builds a portable ZIP (`StorageZilla_<version>_win-x64_portable.zip`) from the published desktop payload.
- ZIP is attached to the GitHub release for no-installer execution scenarios.
- ZIP includes runtime dependencies and binaries from `dotnet publish` output.

## Release Trust + Notes Artifacts
- `StorageZilla-Signing-PublicKey.cer`: public certificate for trust/import workflows.
- `RELEASE-NOTES.md`: release metadata and trust details (subject/thumbprint/expiration).
- `SHA256SUMS.txt` includes checksums for MSIX, MSI, ZIP, and CER assets.

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
