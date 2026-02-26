# AGENTS

## Purpose
This repository contains `Storage Zilla`, a .NET 10 WPF desktop app for Azure Files browsing/sync with an FTP-style dual-pane workflow.

## Environment
- Use Windows `dotnet` when working from WSL `/mnt/*` paths.
- Expected env var: `NEXPORT_WINDOTNET` (example: `"/mnt/c/Program Files/dotnet/"`).
- Build/test with `"$NEXPORT_WINDOTNETdotnet.exe"` in WSL contexts.
- Do not move this repo to a Linux filesystem path.

## Build and Test
- Build solution filter: `dotnet build AzureFilesSync.slnx -c Debug`
- Run all tests: `dotnet test AzureFilesSync.slnx -c Debug`
- Desktop app: `dotnet run --project src/AzureFilesSync.Desktop/AzureFilesSync.Desktop.csproj -c Debug`

## Release Process
- Branch model: `dev` -> `beta` -> `main`.
- `dev` is integration only and does not publish releases.
- `beta` publishes prerelease MSIX artifacts via `.github/workflows/release-beta.yml` on every push.
- `main` publishes stable MSIX artifacts via `.github/workflows/release-prod.yml` on every push.
- Both wrappers call `.github/workflows/release-msix-common.yml`; keep shared release logic there to stay DRY.
- Kick off beta release:
  - merge/promote changes to `beta`
  - `git push -u origin beta` (first time) or `git push origin beta`
- Kick off prod release:
  - merge/promote validated beta changes to `main`
  - `git push origin main`

## Architecture
- `src/AzureFilesSync.Core`: contracts, models, domain services.
- `src/AzureFilesSync.Infrastructure`: Azure SDK integrations, queue/runtime infrastructure.
- `src/AzureFilesSync.Desktop`: WPF UI, view models, dialogs.
- `tests/*`: unit, integration, and UI tests.

## DI Conventions
- Register app services in `src/AzureFilesSync.Infrastructure/ServiceCollectionExtensions.cs`.
- Depend on interfaces from `AzureFilesSync.Core.Contracts`; avoid concrete coupling in UI/viewmodels.
- Use singleton lifetimes by default in this app unless a service is stateful per operation and clearly requires narrower scope.
- Keep constructors explicit; do not use service locators.
- New capability/policy behaviors should be injected as services, not embedded ad hoc in view models.

## Service Conventions
- Keep orchestration in view models thin; business decisions belong in Core services.
- Encapsulate Azure-specific error mapping in infrastructure services (for example `IRemoteErrorInterpreter`), not in UI code.
- Prefer small focused services:
  - capability state evaluation (`IRemoteCapabilityService`)
  - action gating (`IRemoteActionPolicyService`)
  - mirror planning/execution (`IMirrorPlannerService`, `IMirrorExecutionService`)
- Reuse existing services before adding new ones (YAGNI/DTSTTCPW).

## UI Conventions
- `MainViewModel` owns UI state and command wiring; avoid direct SDK calls from code-behind.
- Keep permission and expected access failures non-modal when possible:
  - show remote-pane informational card
  - do not spam modal error dialogs for known permission states
- Keep actionable commands capability-gated:
  - upload/download/mirror buttons should disable when remote side is not accessible.
- Use menu and toolbar actions for primary commands (`Sign In`, `Save Profile`, `Help`, `Settings`).
- Maintain responsive selection flows:
  - cancel previous selection-load operations when a new selection occurs.

## Error and Logging Conventions
- Use `ErrorDialog` for unexpected failures; dialog content must be copyable.
- Use Serilog for info/debug/error with file output.
- Current baseline log level is `Debug`; keep rich context fields (`subscription`, `account`, `share`, `path`) in logs.
- Log location: `%LOCALAPPDATA%/AzureFilesSync/logs`.
- Store temporary scratch artifacts (downloaded logs, throwaway exports, ad hoc diagnostics) in the repo-local `.tmp/` folder.
- Do not leave temp files at repo root; clean up `.tmp/` entries when no longer needed.

## Testing Conventions
- Use AAAA style in tests:
  1. Arrange
  2. Initial Assert
  3. Act
  4. Final Assert
- Test project intent:
  - `AzureFilesSync.Tests`: unit tests for core services and policies
  - `AzureFilesSync.IntegrationTests`: integration-level service behaviors and storage adapters
  - `AzureFilesSync.UITests`: view model command/state behavior
- For new behavior, add tests for both failure and recovery paths (especially permission gating and reconnect/reselection flows).
- Keep assertions strict; do not normalize away bugs in tests.

## Quick Memory MCP
- Project endpoint: `storage-zilla`.
- Use this endpoint for project memory operations (`coldStart`, `searchEntries`, `upsertEntry`, `listRecentEntries`).
- Seed project context before deep work:
  1. `quick-memory.coldStart(endpoint: "storage-zilla")`
  2. `quick-memory.searchEntries(endpoint: "storage-zilla", request: { text: "<topic>" })`

## Testing Style
- Follow AAAA test flow:
  1. Arrange
  2. Initial Assert
  3. Act
  4. Final Assert
