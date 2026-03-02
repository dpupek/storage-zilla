# UI Tour

This page explains what each major section of the main window does.

## 1. Header and Menu
- Top banner: app identity and product context.
- `File` menu: sign in/out, save profile, exit.
- `Tools` menu: transfer and update settings.
- `Help` menu: user guide, update check, about window.

## 2. Azure Selection Row
- `Subscription` dropdown: Azure subscription scope.
- `Storage Account` dropdown: account inside selected subscription.
- `Remote Root` dropdown: unified list of Azure file shares and blob containers inside selected account.

Changing selection refreshes downstream options and updates remote context.

## 3. Path Bars
- Left path bar: local folder path history + editable path.
- `Browse` button: folder picker for local side.
- Local `Up one folder` and `Create local folder` buttons.
- Right path bar: remote relative path history + editable path.
- Remote `Refresh`, `Up one folder`, and `Create remote folder` buttons.

Path rules:
- Root path is shown as `//` on remote.
- `..` row in each grid navigates up one level.

## 4. Local and Remote Grids
- Left grid: local files/folders.
- Right grid: provider-aware entries for selected remote root/path (`Azure Files` or `Azure Blob`).
- Both grids support:
  - Multi-select
  - Sort by clicking column headers
  - Context menus for actions
  - Configurable visible columns

## 5. Remote Search Bar
- `Search remote...` input: recursive remote search query.
- Search scope dropdown:
  - `Current Path`
  - `Share Root`
- Search, cancel, and clear buttons.
- Search progress and scan counts are shown in the remote pane status bar.
- `Go to file location` appears in remote search-result context menu and jumps back to browse mode at that folder.

## 6. Transfer Direction Buttons
- `>>` uploads selected local entries to remote.
- `<<` downloads selected remote entries to local.

## 7. Remote Access Info Card
If remote browsing is unavailable, an informational card overlays the right grid with the reason and corrective direction (for example, missing Azure Files data permissions).

## 8. Transfer Queue Panel
- Queue filter controls: status and direction.
- Queue action buttons:
  - Pause selected
  - Resume selected
  - Retry selected
  - Cancel selected
  - Clear completed/canceled
  - Pause all
  - Run queued jobs
- Grid columns include local path, remote path, direction, conflict policy, status, progress, and message.

## 9. Status Bars
- Signed-in user state
- Throttle setting
- Concurrency setting
- Queue batch summary
- Update status message
- Remote pane status line for search and remote-pane readiness
