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
- [ ] Add bandwidth throttling and per-job concurrency controls.
- [ ] Add packaging and installer profile.

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

## Questions and Decisions
- Decision: Start with temporary case id folder and map to FogBugz later.
- Decision: Keep MVP Windows-only with WPF.
