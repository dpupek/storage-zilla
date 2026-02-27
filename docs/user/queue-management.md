# Queue Management

The queue is the control center for all transfer execution.

## Queue Columns
- `Local`: local source or destination path
- `Remote`: remote Azure Files path
- `Direction`: `Upload` or `Download`
- `Conflict`: effective policy used by the item
- `Status`: `Queued`, `Running`, `Paused`, `Completed`, `Failed`, `Canceled`
- `Progress`: percentage and bytes transferred
- `Message`: latest state/update detail

## Queue Filters
Use filters above the queue grid:
- `Status` filter limits by current state.
- `Direction` filter limits by upload/download.
- `Show All` resets both filters to `All`.

Filtering changes the view only; it does not mutate queue item state.

## Selection and Item Controls
Select one or more queue rows, then use:
- Pause selected
- Resume selected
- Retry selected
- Cancel selected

If no queue rows are selected, these controls are disabled.

## Global Controls
- `Pause all`: pauses all currently active/runnable queue items.
- `Run queued jobs`: starts pending queued items according to current transfer limits.

## Typical Recovery Workflow
1. Filter `Status = Failed`.
2. Inspect `Message` and `Conflict` columns.
3. Select relevant rows.
4. Use `Retry selected`.
5. Monitor progress and clear filters with `Show All`.

## Zero-Byte Items
Completed zero-byte transfers display `100% (0/0)` and are considered successful when status is `Completed`.
