# Transfer recovery review fixes

## Baseline and workflows
- Upload recovery currently deletes invalid checkpoint JSON but can still restore its completed ranges. A changed local source must restart every range.
- Blob and sequential Azure Files downloads save offsets before flushing buffered staging bytes. A saved offset must only cover flushed bytes.
- Cancellation removes checkpoint metadata while a transfer may still hold its staging file open. Cancel and purge must release the worker, remove that job's partial file, and preserve the destination. Pause and retry retain staging data.
- PR checkout currently omits history needed by Nerdbank.GitVersioning.
- Repository scratch cleanup was rejected by command policy. Automatically manage only verified descendants of this repository's `.tmp`; reject links and paths outside that boundary.

## Responsibilities
- `AzureFilesSync.Infrastructure.Transfers.AzureFileTransferExecutor`: reject stale state, flush before checkpointing, and remove its own staging file when abandoned.
- `AzureFilesSync.Core.Services.TransferQueueService`: coordinate worker completion and terminal cleanup through the executor contract.
- `scripts/Remove-RepoTemp.ps1`: validate and remove explicitly named scratch artifacts inside `.tmp`.

## Iteration 1
- [x] Add scoped scratch policy, cleanup helper, and validate boundary checks.
- [x] Fix checkpoint invalidation and buffered-write ordering with executor regressions.
- [x] Fix cancellation/purge cleanup with deterministic lifecycle regressions.
- [x] Fetch full CI history and verify version calculation.
- [x] Run solution build/tests and update user/developer documentation.

## Validation
- Nine new offline integration regression cases cover stale/valid upload ranges through the real executor and Azure SDK, flush-before-checkpoint behavior and failure, cancellation with a held staging handle (including a late I/O failure), paused recovery data, and terminal purge isolation.
- Final Release solution test run after the live-harness changes: 116 passed (35 unit, 42 integration, 39 UI), with 2 disabled live test definitions explicitly skipped. The earlier 117-pass run included the old live test that silently returned when disabled; it was not additional live validation.
- Release build: zero warnings/errors; runtime-specific restore used the CI `win-x64` argument.
- Fresh full-history checkout built with the updated Nerdbank.GitVersioning package; the prior shallow checkout failed version calculation.
- Cleanup checks reject traversal, root deletion, rooted paths, wildcards, alternate data streams, ambiguous trailing characters, and junction traversal. WhatIf, named deletion, and sibling preservation passed.
- Local `storage-zilla-temp.rules` allows the exact validated cleanup helper invocation; a general PowerShell deletion command does not match. Persisted Codex rules load at startup. Review scratch artifacts were removed successfully.
- Live Azure testing was initially disabled. Subsequent results are recorded under Iteration 2 below.

## Decisions
- User authorized fixing the four review findings and automatic `.tmp` management on 2026-09-09.
- User subsequently authorized updating progress and committing the scoped package updates, recovery work, and review fixes. Publishing and release promotion remain separate steps.
- Retain partial downloads for pause/failure recovery; delete them for cancellation and terminal purge only.

## Iteration 2: Live validation
- Baseline: the existing live test silently returns when configuration is absent, covers a single small transfer, uses an in-memory checkpoint, and only cleans up on success. Its direct Azure Files client also omits the OAuth backup intent used by the executor.
- [x] Make live-test gating explicit and cleanup reliable on failure.
- [x] Validate Azure Files against the selected development account using unique temporary paths.
- [x] Exercise real checkpoint interruption/recovery and destination preservation on Azure Files.
- [x] Record Azure Files results and confirm remote test artifacts are removed.
- [x] Complete Blob validation when Blob data access is available.

### Live run: 2026-09-09 (America/Chicago)
- Desktop sign-in: `daniel.pupek@local.darwin-global.net`, WAM. Selected account: `nexportdevstorage`, subscription `NexPort Development and Testing` (`dac48f73-e098-418d-aad5-f2f4f86b0b15`). Automated tests use Azure CLI credentials for the same identity and the real transfer executor; this is not a GUI automation test.
- Azure Files target: `nexportdevstorage/nexportalphashared`. Three live cases passed in 25.48 seconds: protected interrupted download and resume, unchanged-source interrupted upload resume, and changed-source upload restart. All SHA-256 comparisons matched.
- Azure Files cleanup readback: authenticated root listing filtered to `storage-zilla-live-*` returned `[]`.
- Blob target: the app-selected `bootdiagnostics-alphanexw-7aaa6e08-1c18-43f5-bde6-6118240ca74e` container. The initial attempt returned HTTP 403 `AuthorizationPermissionMismatch`. Role readback included Files Privileged Contributor and inherited Owner, but no Blob data role at account or inherited scopes.
- User approved temporary container-scoped Blob Data Contributor access. Created role assignment `7a959f45-1c59-403b-b919-fb07162823b6` for the signed-in user on that container only. After propagation, all three Blob live cases passed (19 seconds): protected interrupted download and resume, unchanged-source staged-block upload resume, and changed-source upload restart. All SHA-256 comparisons matched.
- Blob cleanup readback after successful tests: authenticated listing of the `storage-zilla-live-*` prefix returned `[]`. The pre-test listing after permissions propagated was also empty, confirming no leftover visible test blobs from the denied attempt.
- Removed exactly role assignment `7a959f45-1c59-403b-b919-fb07162823b6`. ARM role-assignment readback for that ID returned `[]`; no other assignments were changed.
- No `.tmp/live-azure-*` payload directories remain. Raw result evidence is retained in `.tmp/live-test-results/live-azure-files.trx`, `live-azure-blob.trx` (initial permission failure), and `live-azure-blob-authorized.trx` (successful rerun). No account-key fallback was used.
- Final live validation: **6 passed, 0 skipped** across Azure Files and Blob. Ordinary offline integration run after the shared live-harness updates: 42 passed; 2 disabled live test definitions explicitly skipped.

## Commit handoff
- Implementation and automated storage validation are complete. The roadmap below separates the remaining desktop acceptance and release gates.
- Final command: `dotnet test AzureFilesSync.slnx -c Release --no-restore` succeeded, rebuilding the affected projects with no reported warnings or errors.
- Dependency audit: `dotnet list AzureFilesSync.slnx package --vulnerable --include-transitive --no-restore --format json` succeeded for all six projects and reported no known vulnerabilities, including transitive packages.
- Security gate policy: **warn-only**. NuGet reported no known vulnerabilities. Gitleaks 8.30.1 scanned `origin/dev..HEAD` (the implementation commit) with redaction and found no secrets. Semgrep 1.176.1 ran 32 applicable OWASP/C# rules on all 19 changed C# files and found no issues. It reported partial-parsing warnings in the two new integration test files for primary-constructor syntax (about 99.9% of lines parsed); no production file had a parsing warning. Those helper declarations were also inspected manually. This limitation keeps the security disposition at **warn**, rather than claiming complete parser coverage.
- Scanner tools and reports are local scratch artifacts under `.tmp/security-tools`. The Gitleaks release archive was verified against its published SHA-256 checksum. Semgrep's native entry point stalled; the supported Python entry point completed using official registry rules downloaded through PowerShell, with metrics and version checks disabled. No source or findings were uploaded to Semgrep.
- No external case was updated: the roadmap uses temporary child identifiers and supplies no explicit tracker destination for this work.
- Created the missing `dev` branch from existing `main` commit `c180297`, pushed the feature branch, and opened [PR #19](https://github.com/dpupek/storage-zilla/pull/19).
- Resolved the desktop automation blocker: the helper conflated the installed executable with the repository build because both have the same name. A renamed copy of the reviewed apphost, verified to have the identical SHA-256 hash and using the same build output, exposed a distinct process identity. The temporary executable was removed after testing.

### Desktop acceptance: 2026-09-09 (America/Chicago)
- Used a synthetic 64 MiB payload in a unique directory under `nexportdevstorage/nexportalphashared` and a repo-local `.tmp` download directory. Initial destination: a 55-byte sentinel.
- Closed the actual WPF process during download. Shutdown persisted the job as paused at 37,748,736 bytes (56.25%); the sentinel was unchanged. Reopened the process, verified the paused queue row and retained checkpoint, signed in, and explicitly resumed it.
- The resumed job completed at 67,108,864 bytes. Destination SHA-256 matched the source (`795035284A13256696874246DEC8D965888F35E647AA508ED98A4FD6DD7F058A`), and no partial file remained.
- Cancellation cleanup passed after pausing a subsequent download at 20,971,520 bytes (31.25%): the UI showed Canceled, the checkpoint and partial file were removed, and the completed destination remained unchanged. Clearing completed/canceled jobs removed all three test queue records while preserving that destination.
- Active-row selection was lost on progress refresh during attempts to cancel a running job. This is existing behavior: the baseline `RefreshQueueItemsFromSnapshot` also clears and recreates every row. The live cancellation result above covers a paused partial download; active-worker cancellation remains covered by the deterministic integration regressions. Preserve row identity/selection in a separate UI follow-up.
- Deleted the unique remote payload and directory; Azure directory existence readback returned `false`. Restored the original connection profile and transfer settings. Evidence remains under `.tmp/desktop-validation-3aef8969dda24e77848dcf52c1401cc9/` (`restart-evidence.json`, `resume-evidence.json`, `cancel-evidence.json`, `purge-evidence.json`).

### Next acceptance and release steps
- [x] Exercise actual WPF close/reopen and explicit resume, plus paused-download cancellation, purge cleanup, and destination preservation.
- [x] Complete code and secret scans (with the parser limitation above), push the branch, and open a PR targeting `dev`.
- [x] Verify hosted Windows CI for PR #19: [run 34431743134](https://github.com/dpupek/storage-zilla/actions/runs/34431743134) passed restore, NuGet audit, Release build, and tests for implementation commit `74e7b3d`.
- [x] Merge PR #19 into `dev` and promote commit `717e204` to `beta`; final PR CI, beta sync guard, and beta release workflow passed.
- [x] Verify the generated MSI upgrade from installed `1.0.23.0` to `1.0.60.0`.
- [x] Correct the MSIX version and missing checksum artifact, rebuild beta, and validate MSIX install/upgrade behavior. Signed-out profile preservation has a separate follow-up below.
- [ ] Promote the validated beta to `main` through the documented release process.

### Installer acceptance: 2026-09-09 (America/Chicago)
- [Beta release v1.0.60-beta.39](https://github.com/dpupek/storage-zilla/releases/tag/v1.0.60-beta.39), commit `717e204b53672d9daae3996bce4b075601a8b44d`, built successfully in [run 34434504025](https://github.com/dpupek/storage-zilla/actions/runs/34434504025). All five downloaded assets matched GitHub's SHA-256 digests.
- MSI upgrade completed successfully with Windows Installer status `0` and no fatal action failure. The log detected and migrated the existing product; registry readback shows `1.0.60.0`, and the previous product registration is absent. Installed desktop product version is `1.0.60+717e204b53`.
- The connection profile was byte-for-byte unchanged after upgrade. The Start Menu shortcut targets the installed executable. Starting that executable produced a responding process with the Storage Zilla main-window title. Computer Use blocked control of `msiexec.exe`, so the user completed setup; subsequent visual app inspection was blocked by `GetCursorPos failed: Access is denied. (0x80070005)` after target refresh/retry. A full post-upgrade visual walkthrough is not claimed.
- MSIX acceptance failed artifact validation: the actual package identity remains `1.0.2.0`, although the workflow computed `1.0.60.10000`. The embedded desktop DLL is current (`1.0.60+717e204b53`). The workflow passes `AppxPackageVersion` but leaves the source manifest version unchanged; write the computed identity version into the manifest and verify the built package before publishing.
- `SHA256SUMS.txt` is missing from the release. The workflow writes it beside the MSIX in a nested package directory, but publishes a path at the parent `msix` directory. The release log confirms that upload pattern matched no files. Publish the exact generated path and fail when a required artifact is absent.
- The MSIX signer matches the manifest publisher, but Windows reports an untrusted root certificate on this machine. No certificate trust settings were changed, and no MSIX installation was attempted alongside the installed MSI.
- Local evidence is retained under `.tmp/beta-installer-validation/`: `artifact-validation.json`, `msi-installed-evidence.json`, `msi-upgrade.log`, and `release-build.log`. Stable promotion remains pending.

### Packaging remediation: 2026-11-13
- Baseline and scope: correct the two release defects recorded above in the shared workflow used by beta and stable releases; retain the existing signing identity and installer behavior.
- [x] Stamp the computed MSIX identity version into the source manifest before packaging.
- [x] Reject missing/multiple MSIX artifacts, an incorrect built identity version, or a publisher/name mismatch before release publication.
- [x] Publish the checksum step's exact output path and fail on unmatched required release assets.
- [x] Validate YAML and execute the actual changed PowerShell blocks with fixtures: stale version, incorrect publisher, missing/multiple packages, valid package, and nested checksum output all behaved as expected.
- [x] Merge through `dev`, rebuild beta, and verify the downloaded manifest and all published checksums.
- [x] Test MSIX installation and startup, recording certificate-trust or installation-mode prerequisites explicitly.
- Fixture validation evidence: `.tmp/check-packaging-workflow.py` and `.tmp/packaging-check-14jntmxr/`. No application code or dependencies changed.
- Packaging fix commit `cb653c3` merged through [PR #20](https://github.com/dpupek/storage-zilla/pull/20) as `989f4bc`. [PR CI 43448655274](https://github.com/dpupek/storage-zilla/actions/runs/43448655274), the beta sync guard, and [beta build 43449119779](https://github.com/dpupek/storage-zilla/actions/runs/43449119779) passed. Release tests: 116 passed, 2 disabled live definitions skipped. Staged Gitleaks scan found no leaks.
- [Beta v1.0.62-beta.40](https://github.com/dpupek/storage-zilla/releases/tag/v1.0.62-beta.40) publishes all six expected assets. The downloaded MSIX declares `1.0.62.10000`; all six GitHub asset digests and all four entries in the published `SHA256SUMS.txt` match. Evidence: `.tmp/msix-acceptance-20261113/artifact-validation.json` and `release-build.log`.
- Actual App Installer inspection shows version `1.0.62.10000`, publisher Unknown, certificate error `0x800B010A`, and a disabled Install button. The signing certificate is unchanged (`CN=Danm@de Software`, thumbprint `51483ACA45D293EBFCC1F91DFA31399C83F9D081`), self-signed, and valid through February 2029; it is not trusted on this workstation. No trust settings were changed.
- Initial installation/startup acceptance was blocked on certificate trust and the existing MSI `1.0.60.0`. The application explicitly rejects simultaneous MSI/MSIX installations. Current profile, checkpoints, and transfer records were backed up before switching installation modes; the successful continuation is recorded below.
- Documentation follow-up: added `docs/user/msix-installation.md` with asset/signer verification, complete certificate-import steps, MSI migration, installation/update checks, and troubleshooting. Root/developer conventions now require a release-tag-specific guide link, current signing details, and separate artifact/install/startup evidence. The future release-notes template corrects Current User trust guidance to Local Machine / Trusted People and lists the MSIX identity version. Executed the template with sample release values and verified its output and relative documentation links; evidence is in `.tmp/install-guide-validation/`. Existing published release notes were not changed.

### MSIX installation and upgrade acceptance: 2026-11-13
- After the user completed the trust change, Authenticode verification returned `Valid` / `Signature verified` for the corrected beta and the previous MSIX. Signer thumbprint remained `51483ACA45D293EBFCC1F91DFA31399C83F9D081`.
- User-assisted removal of MSI `1.0.60.0` completed with Windows Installer exit code `0`; registry readback confirmed the MSI registration was absent. Fresh backups were taken before removal; their path is recorded in `.tmp/msix-acceptance-20261113/current-backup-path.txt`.
- Verified no existing MSIX, installed the hash-verified prior package using `Add-AppxPackage`, and confirmed identity version `1.0.2.0` with package status `Ok`. Opened the corrected beta in App Installer; subsequent readback confirmed an in-place upgrade to `1.0.62.10000`, also `Ok`, under the same package family `StorageZilla.Desktop_43vv7sfxh25ta`.
- Confirmed the running executable came from the upgraded WindowsApps package directory. Visually inspected the main window and About (`1.0.62`), closed the app, relaunched it by package identity, and verified the main window and idle queue again. Azure sign-in/transfers were not rerun as part of this installer check.
- Installer registration, upgrade, and startup checks passed. Profile preservation exposed an existing app defect: signed-out startup changed `SubscriptionId`, `StorageAccountName`, `FileShareName`, and `RemoteRootKind` to null. All other profile values and all eight checkpoint files were unchanged. `LoadLocalProfileDefaultsAsync` calls `LoadLocalDirectoryAsync`, which persists the profile while remote selections are unpopulated; the relevant logic is unchanged from `origin/main`.
- [x] Fix signed-out startup so loading the local pane does not erase saved remote selections; add startup/sign-in restoration regression coverage. Source validation is complete; installed-beta acceptance of this fix remains pending.
- Closed the app and restored the original profile from the fresh backup, verifying identical SHA-256. MSIX remains installed; the app is left closed to preserve the restored profile. No MSI was reinstalled and the user-established certificate trust was retained.
- Evidence: `.tmp/msix-acceptance-20261113/msi-removal.log`, `baseline-install.json`, `upgrade-install.json`, and `final-acceptance.json`. The after-startup profile is retained locally for diagnosis; do not publish it.

### Signed-out profile remediation: 2026-11-13
- Baseline: initial local browsing and later local/settings saves construct a profile from empty remote selectors, overwriting the saved subscription/account/root/kind before sign-in. The legacy-root restoration test uses a read-only store, masking this write. Sign-in can also overlap asynchronous initial profile loading.
- Decision: keep UI profile mapping in the existing view model. Preserve the stored remote selection as a group while no subscription is selected; once a subscription is selected, persist the actual selection, including deliberately empty child selections. Serialize initial profile loading before sign-in and suppress constructor-time saves until defaults have loaded.
- [x] Add writable-store regressions for startup, signed-out local/settings changes, delayed initialization, and sign-in restoration (including Blob and legacy root kinds).
- [x] Implement the scoped persistence fix and run focused/full solution validation.
- [x] Update acceptance/progress and prepare the scoped commit with pending installation documentation and conventions.
- [ ] Publish a new beta containing this fix and the installation guide; verify signed-out profile preservation in that installed build before stable promotion.
- `MainViewModel` now suppresses constructor-time profile writes until defaults load, awaits initial loading before sign-in, and preserves the saved subscription/account/root/kind together while no subscription is selected. A new subscription with empty child selections is saved as-is, without reusing a prior account/root. Saves recheck restoration/selection state after asynchronous profile reads.
- Replaced the legacy read-only profile test store with a writable store. Ten focused cases initially produced **9 failures and 1 pass** on the old code; all ten passed after the fix. Added a further regression for changing subscription with empty child selections. The UI suite increased from 39 to **49 passing cases**.
- Final command: `dotnet test AzureFilesSync.slnx -c Release --no-restore --logger trx --results-directory .tmp/profile-fix-tests/final` passed **126 tests** (35 unit, 42 integration, 49 UI); the two disabled live Azure definitions were skipped. No build warnings/errors were reported. Initial failing and focused passing TRXs are also retained under `.tmp/profile-fix-tests/`.
- Security validation: NuGet audit of all six projects reported no known direct/transitive vulnerabilities. Semgrep 1.176.1 ran 32 applicable rules on both changed C# files with zero findings or parser errors; official local rules were used with metrics disabled. The tool bootstrap used system TLS certificates after its default CA bundle rejected the corporate chain; TLS verification stayed enabled. Reports: `.tmp/security-tools/profile-nuget-audit.json` and `semgrep-profile-report.json`.
- Release-note generation and documentation-link checks passed using `.tmp/check-installation-docs.py`; `git diff --check` passed. Existing mixed line endings in the UI test file were preserved outside changed lines.
- Gitleaks 8.30.1 scanned the staged change set with redaction and found no secrets (`.tmp/security-tools/gitleaks-profile-report.json`). The pre-commit security gate's configured policy is warn-only; all executed checks completed with zero findings.
- Scope: this commit also records completed MSIX installer acceptance, adds the requested installation/trust guide and navigation links, updates future release notes, and captures the conventions. The installed/published `v1.0.62-beta.40` still contains the old startup bug; it was not replaced during this source-fix task. No cloud data, installed application, or certificate trust was changed.
