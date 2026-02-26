# Edge Cases

## Auth
- User closes login browser window.
- Token expires during transfer.

## Discovery
- No subscriptions available.
- User has ARM rights but no data-plane file share rights.
- Remote entry selection can race with async metadata enrichment.

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

## Mitigations
- Explicit error messages and retry controls.
- Checkpoint persistence for resume.
- Mandatory delete confirmation for mirror deletes.
- Preserve right-pane selection identity while async metadata updates complete.
- Transfer queue dedupe blocks duplicate active transfers (`Queued`/`Running`/`Paused`) for the same direction + source + destination.
- Queue filtering controls (status + direction + show all) improve recovery triage without mutating queue state.
- Configurable conflict policies (`Ask/Skip/Overwrite/Rename`) with separate upload/download defaults.
- `Ask` conflict prompt resolves decisions before queueing, with `Do for all` scoped to current selected batch.
- Runtime guard cancels unresolved `Ask` conflicts with explanatory queue message.
- Worker claim logic ensures single queue run can drain queued jobs.
- Completed zero-byte transfers render `100%` to avoid false incomplete signal.
