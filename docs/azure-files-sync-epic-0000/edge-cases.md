# Edge Cases

## Auth
- User closes login browser window.
- Token expires during transfer.
- Broker-based auth prerequisites are missing on a workstation.
- Localhost callback page can show connection-refused after successful browser sign-in redirect.

## Discovery
- No subscriptions available.
- User has ARM rights but no data-plane file share rights.
- Remote entry selection can race with async metadata enrichment.
- Saved storage account/file share endpoint is no longer resolvable (DNS/network drift).

## Transfers
- Local file deleted while queued.
- Mid-transfer network interruption.
- Partial file exists from earlier failed transfer.
- User queues the same source/destination pair multiple times accidentally.
- Conflict discovered before queueing (destination already exists).
- Conflict appears after queueing because destination changed before execution.
- Zero-byte transfers can look incomplete if progress formatting is naive.

## Mirror
- Delete operations included accidentally.
- Large directory trees causing long planning time.

## Search
- Query returns a few early matches then scans a very large low-match directory; buffered match updates can delay visible UI progress if not flushed.
- User cancels a long-running search and immediately starts a new one; stale read work can leak into perceived status without strict latest-only scheduling.
- Search status near top command controls can be missed during long scans if users focus on grid results.

## UI Layout
- WPF `ToolBar`/`ToolBarTray` overflow behavior can collapse or unpredictably size embedded stretch controls (combo/text inputs), causing unusable command rows.

## Mitigations
- Explicit error messages and retry controls.
- Checkpoint persistence for resume.
- Mandatory delete confirmation for mirror deletes.
- Interactive auth now prefers WAM and falls back to system browser automatically when broker requirements are not met.
- Profile restore/share discovery errors from unavailable hosts are surfaced as non-blocking guidance so sign-in remains usable.
- Preserve right-pane selection identity while async metadata updates complete.
- Transfer queue dedupe blocks duplicate active transfers (`Queued`/`Running`/`Paused`) for the same direction + source + destination.
- Queue filtering controls (status + direction + show all) improve recovery triage without mutating queue state.
- Configurable conflict policies (`Ask/Skip/Overwrite/Rename`) with separate upload/download defaults.
- `Ask` conflict prompt resolves decisions before queueing, with `Do for all` scoped to current selected batch.
- Runtime guard cancels unresolved `Ask` conflicts with explanatory queue message.
- Worker claim logic ensures single queue run can drain queued jobs.
- Completed zero-byte transfers render `100%` to avoid false incomplete signal.
- Remote search now flushes partial match batches on timed heartbeats so UI progress/match visibility does not stall during long non-match scans.
- Remote read scheduler enforces latest-operation semantics to avoid overlap when canceling and restarting remote searches.
- Remote folder paging/navigation now enforces cancellation checkpoints before capability/page state mutations to prevent stale operations from resetting the current path.
- Remote path dropdown text is synchronized from authoritative view-model state after remote refreshes so large folders with `Load more` do not flash and clear the address field.
- Remote search status is anchored in a bottom pane status bar for persistent visibility during long scans.
- Use deterministic command-bar layout (`Border + Grid`, star/auto columns) instead of WPF `ToolBarTray` for pane controls that require reliable stretching.
- Centralize path parsing/formatting via `IPathDisplayFormatter` so root and separator behavior stays consistent across view model and UI bindings.
- Route remote browse/search/load-more/selection loads through `IRemoteOperationCoordinator` to retain latest-only semantics plus explicit cancel reasons in logs.
- Marshal command-state notifications (`NotifyCanExecuteChanged`) through the dispatcher to avoid thread-affinity faults when async operations complete on non-UI continuations.
