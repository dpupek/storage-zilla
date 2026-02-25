# Storage Zilla

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
