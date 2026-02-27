# Buttons and Actions Reference

This reference maps visible controls to their behavior.

## Menu Actions

### File
- `Sign In`: interactive Azure login.
- `Sign Out`: clears current authenticated session.
- `Save Profile`: persists selected subscription/account/share, paths, and settings.
- `Exit`: closes app.

### Tools
- `Settings`: opens transfer/update settings dialog.

### Help
- `Help`: opens in-app user guide window.
- `Check for Updates...`: checks for newer release and offers release-page open.
- `About`: version, license links, and update shortcut.

## Path Controls
- Local path combo:
  - Editable path input
  - Recent path history
- `Browse`: pick local folder with system folder picker.
- Remote path combo:
  - Editable relative share path
  - Recent path history
- `Refresh`: requery current remote directory.

## Transfer Buttons (center)
- `>>` Upload selected local entry to remote target context.
- `<<` Download selected remote entry to current local path.

## Queue Action Buttons
- Pause selected
- Resume selected
- Retry selected
- Cancel selected
- Pause all
- Run queued jobs

Selected-item actions operate only on selected queue rows.

## Local Grid Context Menu
- Upload selection (start now)
- Upload selection (add to queue)
- Show in Explorer
- Open
- Open with...
- Rename
- Delete
- Column toggles: Name, Type, Size, Modified, Date Created, Author

## Remote Grid Context Menu
- Download selection (start now)
- Download selection (add to queue)
- Rename
- Delete
- Column toggles: Name, Type, Size, Modified, Date Created, Author

## Status Signals
- Right-side informational card: remote capability/permission guidance.
- Bottom status bar: login, throttle, concurrency, queue summary, update status.
