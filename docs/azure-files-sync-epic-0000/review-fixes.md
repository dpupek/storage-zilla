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
- Created the missing `dev` branch from existing `main` commit `c180297`, pushed the feature branch, and opened [draft PR #19](https://github.com/dpupek/storage-zilla/pull/19). The PR remains draft pending desktop acceptance.
- Desktop automation could enumerate the running Release app but failed to resolve its window on both attempts: `window id 35455300 no longer belongs to ...; current owner is ...` (both owner strings identified Storage Zilla). The app was left running; no desktop close/reopen or cancel/purge pass is claimed.

### Next acceptance and release steps
- [ ] Exercise actual WPF close/reopen and explicit resume, plus cancel/purge cleanup and destination preservation. Live tests reopen persistence and executor instances but do not terminate and restart the desktop process.
- [x] Complete code and secret scans (with the parser limitation above), push the branch, and open a PR targeting `dev`.
- [x] Verify hosted Windows CI for PR #19: [run 34431743134](https://github.com/dpupek/storage-zilla/actions/runs/34431743134) passed restore, NuGet audit, Release build, and tests for implementation commit `74e7b3d`.
- [ ] Promote accepted changes to `beta` and smoke-test the generated MSI/MSIX install and upgrade paths.
- [ ] Promote the validated beta to `main` through the documented release process.
