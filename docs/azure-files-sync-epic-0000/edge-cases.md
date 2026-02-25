# Edge Cases

## Auth
- User closes login browser window.
- Token expires during transfer.

## Discovery
- No subscriptions available.
- User has ARM rights but no data-plane file share rights.

## Transfers
- Local file deleted while queued.
- Mid-transfer network interruption.
- Partial file exists from earlier failed transfer.

## Mirror
- Delete operations included accidentally.
- Large directory trees causing long planning time.

## Mitigations
- Explicit error messages and retry controls.
- Checkpoint persistence for resume.
- Mandatory delete confirmation for mirror deletes.
