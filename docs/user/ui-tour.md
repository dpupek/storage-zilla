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
- `File Share` dropdown: Azure file share inside selected account.

Changing selection refreshes downstream options and updates remote context.

## 3. Path Bars
- Left path bar: local folder path history + editable path.
- `Browse` button: folder picker for local side.
- Right path bar: remote relative path history + editable path.
- `Refresh` button: reload current remote directory.

Path rules:
- Root path is shown as `\` on remote.
- `..` row in each grid navigates up one level.

## 4. Local and Remote Grids
- Left grid: local files/folders.
- Right grid: Azure Files entries for selected share/path.
- Both grids support:
  - Multi-select
  - Sort by clicking column headers
  - Context menus for actions
  - Configurable visible columns

## 5. Transfer Direction Buttons
- `>>` uploads selected local entries to remote.
- `<<` downloads selected remote entries to local.

## 6. Remote Access Info Card
If remote browsing is unavailable, an informational card overlays the right grid with the reason and corrective direction (for example, missing Azure Files data permissions).

## 7. Transfer Queue Panel
- Queue filter controls: status and direction.
- Queue action buttons:
  - Pause selected
  - Resume selected
  - Retry selected
  - Cancel selected
  - Pause all
  - Run queued jobs
- Grid columns include local path, remote path, direction, conflict policy, status, progress, and message.

## 8. Status Bar
- Signed-in user state
- Throttle setting
- Concurrency setting
- Queue batch summary
- Update status message
