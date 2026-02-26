# Roadmap

## Phase 1: Foundation (epic-0000)
- [x] Create .NET 10 solution and projects.
- [x] Add dependency injection and logging bootstrap.
- [x] Define core contracts and models.

## Phase 2: Azure Discovery + Browsing (child-0001)
- [x] Implement interactive auth service.
- [x] Implement subscription/storage/file share discovery.
- [x] Wire left/right pane directory loaders.

## Phase 3: Transfer Queue (child-0002)
- [x] Implement transfer queue service.
- [x] Implement checkpoint store.
- [x] Implement upload/download executor.
- [x] Add pause/resume command UX.

## Phase 4: Mirror Sync (child-0003)
- [x] Implement mirror planner.
- [x] Add mirror preview summary and delete confirmation.
- [x] Queue mirror operations.

## Phase 5: Hardening (child-0004)
- [x] Add transient retry handling for Azure file transfer operations.
- [x] Add resume safety checks and integrity verification on downloads when remote hash is available.
- [x] Add richer error categorization.
- [x] Add bandwidth throttling and per-job concurrency controls.
- [x] Add packaging and installer profile.
- [x] Add branch-based release pipelines for `beta` (prerelease) and `main` (stable) with signed MSIX publishing.
- [x] Align About dialog version display with computed build version metadata.
- [x] Harden reusable release workflow error handling for runner/SDK/certificate failures.
- [x] Add release helper script to rotate MSIX GitHub secrets from a validated PFX export.

## Phase 7: Live Integration Coverage (child-0006)
- [x] Add env-gated live Azure Files upload/download integration test.
- [x] Add live resume-from-checkpoint verification path.

## Phase 6: Connection Profiles (child-0005)
- [x] Persist subscription/account/share + local/remote paths.
- [x] Add recent local/remote target lists in UI.

## Phase 8: UX and Operations (child-0007)
- [x] Add top application menu for sign in/sign out, save profile, settings, and help.
- [x] Add remote capability gating and informational permission card in the right pane.
- [x] Disable mirror actions until remote side capabilities allow planning/execution.
- [x] Add context menus for local/remote grids with single-item operations (open/show in explorer/open with/rename/delete).
- [x] Add per-grid column picker with sorting support.
- [x] Add `Date Created` and `Author` columns in local and remote panes.
- [x] Add enhanced error dialog support with copy-to-clipboard details.
- [x] Add debug/info/error filesystem logging via Serilog.
- [x] Publish workspace to GitHub repository `dpupek/storage-zilla`.
- [x] Stabilize remote selection during async metadata enrichment.
- [x] Replace boolean file type display with `Parent`/`Folder`/`File`.
- [x] Format file sizes as human-readable units.
- [x] Display modified/created timestamps in local time.
- [x] Add compact status bar with sign-in state, throttle, and concurrency.
- [x] Add labels above subscription/storage account/file share selectors.
- [x] Prevent duplicate active queue items for the same transfer identity (direction + source + destination).
- [x] Add queue status/direction filters with `Show All` reset.
- [x] Enable horizontal scrolling for local and remote file grids.
- [x] Alphabetically sort subscription, storage account, and file share selectors.
- [x] Add explicit conflict policies (`Ask`, `Skip`, `Overwrite`, `Rename`) for upload/download queueing.
- [x] Add transfer conflict settings for upload/download defaults and persist them in connection profile.
- [x] Add pre-queue conflict prompt with batch-scoped `Do for all` handling and `Cancel Batch`.
- [x] Store effective conflict decision on queued request and surface conflict policy in queue grid.
- [x] Add runtime safety: unresolved `Ask` conflict at execution cancels item with explanatory message.
- [x] Fix worker-claim race so single `Run Queue` drains queued items reliably.
- [x] Correct queue progress display for zero-byte completed transfers (`100% (0/0)`).
- [x] Relabel queue columns to explicit `Local` and `Remote` paths.
- [x] Temporarily hide mirror planning/execution controls pending dedicated mirror conflict UX.
- [x] Expand end-user documentation into task-oriented guides (getting started, UI tour, transfers, queue, controls, troubleshooting).
- [x] Replace static help popup with embedded in-app help docs viewer.
- [x] Ensure embedded help supports internal doc navigation and local image rendering.
- [x] Enforce single-instance desktop app startup.

## Phase 9: Update Distribution (child-0008)
- [x] Add in-app manual update check command in Help/About.
- [x] Add GitHub latest stable release lookup and installer asset discovery.
- [x] Add update download + SHA256 + publisher/version validation before install launch.
- [x] Add update channel selection (`Stable`/`Beta`) and persist it in profile/settings.
- [x] Update in-app update check UX to open the newer GitHub release page directly.
- [x] Add unsigned MSI artifact publishing alongside signed MSIX in release pipeline.
- [x] Add MSI install UX: license dialog, install directory selection, Start Menu shortcut.
- [x] Brand MSI welcome/license banner assets from project logo set.

## Questions and Decisions
- Decision: Start with temporary case id folder and map to FogBugz later.
- Decision: Keep MVP Windows-only with WPF.
