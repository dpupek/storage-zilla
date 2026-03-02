---
name: azure-files-search-reliability
description: Reliability checklist for Azure Files recursive search in Storage Zilla. Use when search appears stalled, mismatched with UI progress, or behaves inconsistently after cancel/restart.
---

# Azure Files Search Reliability

Use this skill when working on remote search behavior, especially for large Azure Files shares.

## Goals
- Keep search responsive for users during long traversals.
- Avoid stale/canceled run bleed-through.
- Make diagnostics decisive so support can distinguish slow vs stalled.

## Required Design Rules
1. Keep search incremental (`IAsyncEnumerable`) and emit progress frequently.
2. Flush partial match buffers on a timer; do not depend only on large match batches.
3. Emit heartbeat progress even when no new matches appear.
4. Serialize remote read operations and enforce latest-only behavior.
5. Treat superseded/canceled operations as expected debug-level outcomes.

## Diagnostics Contract
For each search run, log:
- Run lifecycle: `requested`, `progress`, `completed`/`finished`, and `stale` ignores.
- Correlation: `RunId`, run version, query, account/share/path/scope.
- Paging: directory name, continuation-token presence, entries/page, elapsed ms.
- Counters: `ScannedEntries`, `ScannedDirectories`, total matches.

## Implementation Checklist
- `MainViewModel`:
  - Maintain run versioning and stale-run guards.
  - Preserve cancellation semantics and clear status messaging.
  - Avoid modal errors for expected cancel/stale paths.
- `RemoteReadTaskScheduler`:
  - Cancel previous operation.
  - Await prior execution settle before starting latest operation.
  - Keep latest-only state with version checks.
- `RemoteSearchService`:
  - Use timed heartbeat emission.
  - Flush buffered matches on timer during non-match-heavy scans.
  - Continue traversal through scoped, expected per-directory failures when safe.

## Regression Tests (AAAA)
Add/maintain tests for:
- Sparse early matches + long non-match traversal still updates progress.
- Cancel long search then immediately start another search; latest search owns UI state.
- Completion and truncation behavior with max-results cap.
- Denied/missing child directory skip behavior (does not fail entire search).

## Triage Playbook
1. Reproduce with one long query (for example `nexdata`) and one sparse query (for example `user`).
2. Confirm whether backend paging is progressing in logs.
3. If backend progresses but UI stalls:
   - verify timed heartbeat + buffered match flush.
   - verify stale-run version checks and scheduler serialization.
4. Capture findings in quick-memory endpoint `storage-zilla`.

## Do Not
- Do not “fix” stalls by weakening assertions or hiding status updates.
- Do not terminate scans early just because matches were found quickly.
- Do not rely on raw exception popups for expected cancellation/supersede flows.
