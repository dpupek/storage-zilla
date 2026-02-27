# Troubleshooting

## Sign-in Works But Remote Browse Fails
Symptom:
- Right pane shows permission warning
- Azure error includes `AuthorizationPermissionMismatch`

Cause:
- Identity lacks Azure Files data permissions on target scope.

Fix:
1. Assign Azure Files data-plane role on storage account or share scope.
2. Recommended roles:
   - `Storage File Data Privileged Reader`
   - `Storage File Data Privileged Contributor`
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

## Update Check Opens Release Page
Current behavior:
- `Check for Updates...` compares versions and offers to open the GitHub release page for the newer version.
- Install method can be chosen from released assets for your environment.

## Where Logs Are Stored
- `%LocalAppData%\AzureFilesSync\logs\desktop-*.log`

Use logs for detailed exception context when reporting issues.
