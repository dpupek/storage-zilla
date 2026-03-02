# Troubleshooting

## Sign-in Works But Remote Browse Fails
Symptom:
- Right pane shows permission warning
- Azure error includes `AuthorizationPermissionMismatch`

Cause:
- Identity lacks required data permissions on target scope.

Fix for Azure File Shares:
1. Assign Azure Files data-plane role on storage account or share scope.
2. Recommended roles:
   - `Storage File Data Privileged Reader`
   - `Storage File Data Privileged Contributor`
3. Refresh remote pane after role propagation.

Fix for Blob Containers:
1. Assign Blob data-plane role on storage account or container scope.
2. Recommended roles:
   - `Storage Blob Data Reader`
   - `Storage Blob Data Contributor`
3. Refresh remote pane after role propagation.

## Access Denied for Local Folder
Symptom:
- Local browse fails with access denied (for example restricted system paths).

Fix:
- Navigate to a readable folder.
- Use elevated privileges only when required by your environment policy.

## Queue Item Did Not Start
Checks:
1. Verify row status is `Queued`.
2. Click `Run queued jobs`.
3. Confirm queue filters are not hiding rows.
4. Check message column for conflict or permission details.

## Duplicate Transfer Not Added
Expected behavior:
- Active duplicate transfers (same direction + source + destination) are blocked to avoid accidental repeated operations.

## Path Appears But Grid Does Not Change
Checks:
1. Ensure subscription/account/share selection is valid.
2. Confirm the right pane does not show permission info card.
3. Use `Refresh` on remote path.
4. Re-select the target path or parent `..` navigation.

If a typed/selected remote path does not exist:
- Storage Zilla warns and restores the previous valid path/grid view.

## Update Check Opens Release Page
Current behavior:
- `Check for Updates...` compares versions and offers to open the GitHub release page for the newer version.
- Install method can be chosen from released assets for your environment.

## Where Logs Are Stored
- `%LocalAppData%\AzureFilesSync\logs\desktop-*.log`

Use logs for detailed exception context when reporting issues.

## Remote Search Appears Slow
Notes:
- Search is recursive and provider-aware (Files + Blob).
- Results stream incrementally during scan; progress is shown in the remote pane status bar.

Tips:
1. Use `Current Path` scope when possible for faster results.
2. Use cancel to stop a long scan and refine query/scope.
3. Use `Go to file location` from search results to switch back to browse mode at that folder.
