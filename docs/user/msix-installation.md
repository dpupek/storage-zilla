# Install Storage Zilla with MSIX

Use this guide when installing or updating the signed MSIX package, especially when App Installer shows **Publisher: Unknown**, disables **Install**, or reports that the publisher certificate could not be verified.

## Choose an installer

| Format | Use | Requirements |
| --- | --- | --- |
| MSIX | Packaged Windows installation and updates | Windows App Installer and a trusted signing certificate; an administrator may need to establish trust first. |
| MSI | Manual installation using the traditional setup wizard | Administrator access. Release assets identify this installer as unsigned. |
| Portable ZIP | Extracted application files | Keep the extracted files together. This does not install or trust the MSIX certificate. |

Keep one installed format. Storage Zilla refuses to run when it detects both MSI and MSIX installations. If MSI is already installed, follow [Switch from MSI](#switch-from-msi) before launching the MSIX version.

## Download and verify the release

1. Open the project's [GitHub releases](https://github.com/dpupek/storage-zilla/releases). Choose the intended stable release or a release explicitly marked as a beta prerelease.
2. From that release's **Assets**, download the `.msix`, `StorageZilla-Signing-PublicKey.cer`, and `SHA256SUMS.txt`. Keep them in the same folder. Use the certificate from the same release, since certificates can change between releases.
3. Read the release notes' **Signing Certificate** section. It gives the expected subject, thumbprint, and expiration. Do not rely on a thumbprint copied from an older guide or conversation.
4. Verify the downloaded MSIX and CER against their matching filenames in `SHA256SUMS.txt`. In PowerShell, change to your download folder and run these read-only commands:

   ```powershell
   Get-FileHash -LiteralPath '.\StorageZilla-Signing-PublicKey.cer' -Algorithm SHA256
   Get-FileHash -LiteralPath '.\YOUR-DOWNLOADED-PACKAGE.msix' -Algorithm SHA256
   Get-Content -LiteralPath '.\SHA256SUMS.txt'
   ```

   Replace `YOUR-DOWNLOADED-PACKAGE.msix` with the actual filename. Each computed hash must match the corresponding entry. Certificate thumbprints and file SHA-256 hashes are different values; compare each with its own expected value.
5. Double-click the CER. Check the subject and validity dates on **General**, and **Thumbprint** on **Details**, against the release notes. The package's **Properties → Digital Signatures → Details → View Certificate** should identify the same signer.

If a hash or signer differs, stop and download the assets again from the intended release. If the difference remains, report it to the maintainer. An expired certificate or modified package needs a corrected release; importing a certificate is not the remedy.

## Trust the verified certificate

If Windows already trusts the signer, continue to [Install or update](#install-or-update). For the project's self-signed certificate, an administrator must establish trust on the computer. This trusts packages signed by that certificate for all users, not just this one download.

1. Open the verified `StorageZilla-Signing-PublicKey.cer` from your download folder.
2. Select **Install Certificate… → Local Machine → Next**. Approve the Windows elevation prompt or have your administrator complete it.
3. Select **Place all certificates in the following store**.
4. Select **Browse → Trusted People → OK**.
5. Select **Next → Finish** and confirm the import succeeded.
6. Close the certificate window and any open App Installer window. Reopen the downloaded MSIX to refresh validation.

Use **Local Machine → Trusted People**, even for a per-user MSIX installation. **Current User** alone is insufficient. Do not select **Trusted Root Certification Authorities** for this signing certificate. These steps follow [Microsoft's package-signature troubleshooting guidance](https://learn.microsoft.com/windows/win32/appxpkg/how-to-troubleshoot-app-package-signature-errors).

On a managed workstation, ask your administrator to deploy the approved certificate if you cannot import it. Do not disable signature verification or change device policy to get past the error.

## Switch from MSI

1. Finish or pause active transfers, save your profile, and close Storage Zilla.
2. Back up `%LOCALAPPDATA%\AzureFilesSync` to a location you control. Keep any local files used by unfinished transfers; a settings backup does not contain those files. Do not upload this backup to a public issue.
3. Open **Windows Settings → Apps → Installed apps** (called **Apps & features** on some Windows versions).
4. Find the existing Storage Zilla MSI installation, select **Uninstall**, and complete setup/elevation prompts. Confirm it is removed before continuing.
5. Install the MSIX below. Check settings and paths before resuming work; migration of MSI settings into the packaged app is not an accepted/tested guarantee.

Known limitation in beta `v1.0.62-beta.40`: signed-out startup clears saved subscription, storage account, and remote-root selections. The source fix has regression coverage and is awaiting a new beta release. When using that older beta, keep the backup and expect to reselect the remote connection after signing in. Local path/settings and checkpoint files were preserved in the installer test.

## Install or update

1. Close Storage Zilla, then double-click the verified MSIX.
2. Confirm the version matches the package version in the release notes and that the publisher is recognized. Release tags such as `v1.0.62-beta.40` and Windows package versions such as `1.0.62.10000` use different formats.
3. Select **Install**, or **Update** for an existing older MSIX installation. Let Windows complete deployment, then launch Storage Zilla from Start.
4. Confirm the app opens, inspect its version under **Help → About**, and check the connection profile and transfer queue before running transfers.

An existing MSIX installation should be updated with a newer package of the same identity. If a newer version is already installed, use a newer release or arrange a backed-up rollback; do not uninstall merely to force a downgrade.

## If installation is still blocked

| Symptom | Next check |
| --- | --- |
| Publisher Unknown; `0x800B010A` or `0x800B0109` | Confirm the correct release certificate was imported into **Local Machine → Trusted People**, then reopen App Installer. |
| Certificate was imported into Current User | Repeat the verified import into Local Machine. |
| Certificate expired, signer differs, or checksum differs | Stop and obtain a corrected or matching release. Additional trust does not repair the package. |
| Device policy or administrator access blocks installation | Ask the workstation administrator to approve/deploy the package and certificate. |
| Both MSI and MSIX detected on startup | Back up state and remove the installation type you no longer intend to use. |
| MSIX does not open in App Installer | Ask your administrator to install or repair Microsoft App Installer. |

For support, record the release tag, exact MSIX filename, Windows version, complete error text/code, and certificate thumbprint/store. Include the relevant deployment event from **Event Viewer → Applications and Services Logs → Microsoft → Windows → AppXDeployment-Server → Operational** if available. Keep account details and local paths out of public screenshots.

If the certificate was trusted only for temporary testing, ask your administrator to remove that exact certificate from **Local Computer → Trusted People** when it is no longer needed. Removing trust can affect subsequent installs and updates signed by it.

Continue with [Getting Started](getting-started.md) after installation.
