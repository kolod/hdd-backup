# Contract: UI for shrinking

## Restore / Verify tab

- New button **Shrink image…** (enabled when an image is selected) → Shrink dialog.
- When **Write to device…** is pressed and the image is larger than the target:
  1. `ShrinkPlanner.Analyze` runs; this takes seconds and shows a busy cursor.
  2. If it is shrinkable and fits, a dialog shows: image size, card size, used space in the
     root file system, and the size after shrinking. Buttons: **Shrink and write** / **Cancel**.
  3. If it is not shrinkable, the existing "image does not fit" error is shown, plus the
     blocking reason.
  4. **Shrink and write** → output path is chosen automatically next to the source
     (`<name>_shrunk_<size>.img`) → normal confirm-write dialog → combined progress → result
     banner names both the shrunk image file and the write result.

## Shrink dialog (standalone)

| Element | Content |
|---------|---------|
| Summary | Image size, layout (partitions), root FS used space, minimum size |
| Target | Radio: **Smallest possible** / **Fit a card of size:** [number] [MB/GB] |
| Output | Path + Browse (`.img` / `.img.gz`) |
| Blockers | Red list of reasons when not shrinkable; Start disabled |
| Buttons | **Start** / **Cancel** |

Progress uses the existing operation panel (phases: Analyzing, Copying data, Checking,
Verifying, Committing).

## New string keys (all six languages)

`Restore_Shrink`, `Shrink_Title`, `Shrink_Summary`, `Shrink_Minimum`, `Shrink_TargetMinimum`,
`Shrink_TargetCard`, `Shrink_Output`, `Shrink_Start`, `Shrink_OfferTitle`, `Shrink_OfferText`,
`Shrink_AndWrite`, `Shrink_ResultSaved`, `Phase_Analyzing`, `Phase_Rebuilding`, `Phase_Checking`,
`Blocker_*` (one per ShrinkBlocker), `Error_ShrinkVerifyFailed`.
