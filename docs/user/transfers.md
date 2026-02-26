# Transfers

This page covers upload and download workflows, including conflict behavior.

## Upload Workflow
1. In the left grid, select one or more files.
2. Use one of:
   - `>>` button
   - Local context menu `Upload Selection (Start Now)`
   - Local context menu `Upload Selection (Add to Queue)`
3. Confirm items appear in `Transfer Queue`.
4. If needed, click `Run queued jobs`.

`Start Now` requests immediate execution. `Add to Queue` stages work without immediate start.

## Download Workflow
1. In the right grid, select one or more files.
2. Use one of:
   - `<<` button
   - Remote context menu `Download Selection (Start Now)`
   - Remote context menu `Download Selection (Add to Queue)`
3. Confirm queue status/progress and destination local path.

## Multi-Select Behavior
- Multi-select in either grid creates one queue item per file.
- Duplicate active transfers are blocked for the same direction/source/destination identity.

## Conflict Policies
Each queued transfer carries an effective conflict policy:
- `Ask`: prompt when conflict is detected
- `Skip`: do not transfer conflicting item
- `Overwrite`: replace destination
- `Rename`: auto-generate non-conflicting target name

Default upload/download policies are configured in `Tools -> Settings`.

## Ask Prompt and "Do for all"
When policy is `Ask` and a conflict occurs:
- You can choose `Overwrite`, `Rename`, `Skip`, or cancel batch.
- `Do for all` applies your choice to remaining items in that selected batch.

## Mirror Planning
Mirror planning is a separate workflow from one-off transfer actions.
- Build a mirror plan first.
- Queue or execute mirror operations after plan review.
