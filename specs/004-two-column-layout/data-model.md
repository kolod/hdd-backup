# Data Model: Two-Column Layout and an Image Tab

No persistent data changes. `HddBackup.ini` keeps the same settings; the Image tab does not add
any. The entities below are in-memory UI state and one pure Core decision.

## SelectedImage (App layer, shared by the Restore and Image tabs)

| Field | Type | Notes |
|-------|------|-------|
| `Path` | string | Trimmed path as typed or chosen; empty when none |
| `Descriptor` | `ImageDescriptor` or null | Loaded image (format, kind, sizes, checksum sidecars, partition metadata) |
| `LoadError` | string or null | Localized reason when loading failed |
| `RequiredDeviceBytes` | long | End of the last partition (+ GPT tail); 0 when unknown. Full-device images only |
| `Analysis` | `ShrinkPlan` or null | Result of `ShrinkPlanner.Analyze` |
| `AnalysisState` | enum `None`, `Running`, `Done`, `Failed` | `None` for no image or a partition image |
| `AnalysisError` | string or null | Message when `Failed` |

**Events**: `Changed` (raised on the UI thread after any field changes).

**Operations**:

- `SetPath(path)`: no-op when unchanged; otherwise cancels a running analysis, loads the
  descriptor, computes `RequiredDeviceBytes`, starts the analysis for full-device images, raises
  `Changed`.
- `Reload()`: re-reads the descriptor and sidecars for the same path (after checksum files were
  saved); keeps a finished analysis when the file is unchanged (same length and write time).

**State transitions of the analysis**:

```text
None ──SetPath(full image)──► Running ──finished──► Done
                                 │  └──exception──► Failed
                                 └──SetPath(other)──► (cancelled) ──► None / Running (new image)
```

## ShrinkTarget (Image tab)

| Choice | `ShrinkTargetBytes` sent to the engine | Available when |
|--------|-----------------------------------------|----------------|
| Smallest possible | null | analysis `Done` and shrinkable |
| Size of a device | `DeviceInfo.Size` of the chosen device | at least one device listed |
| Card size | value × (GiB or MiB) | always (value 1 … 2 097 152) |

**Validation**: **Create smaller image** is enabled only when the analysis is `Done`, the plan
is shrinkable, `plan.Fits(target)` holds (smallest possible always fits), the output path is not
empty and is not the source path. When the target is too small, the reason
(`Blocker_NotEnoughSpaceEvenAtMinimum`) is shown instead of a message box.

## ShrinkOutputName (Core, moved from `ShrinkDialog.DefaultOutputPath`)

`Default(source, targetBytes?, gzip)` → `<dir>\<name>_shrunk[_<N.N>G].img[.gz]`. Unchanged
naming rule; now shared by the Image tab and the "Shrink and write" flow.

## ImageFit (Core, pure function)

Inputs: image format (raw / gzip), file size, required device bytes, analysis (optional), device
size.

| Result | Condition | Restore tab warning |
|--------|-----------|---------------------|
| `Fits` | required ≤ device (for raw: file size ≤ device, or only unused space after the last partition is past the end) | none |
| `Pending` | larger, analysis not finished | "The image is larger than the device." |
| `ShrinkToFit` | larger, analysis done and `plan.Fits(device)` | "… It will be shrunk to fit before writing." |
| `CannotFit` | larger, analysis failed, not shrinkable, or minimum > device | "… It cannot be shrunk to fit: <reasons>" |

`MainForm` uses the same result before offering "Shrink and write" (it waits for the analysis
when the result is `Pending`), so the warning and the offer agree.

## Tooltip map

| Control | Label key | Tooltip key |
|---------|-----------|-------------|
| Backup scope "Whole device" | `Backup_ScopeWhole` | `Tooltip_ScopeWhole` |
| Format list, compressed selected (Backup, Image) | `Format_Gzip` | `Tooltip_FormatGzip` |
| Format list, uncompressed selected (Backup, Image) | `Format_Raw` | `Tooltip_FormatRaw` |
| Fast compression | `Option_FastCompression` | `Tooltip_FastCompression` |
| Back up anyway | `Option_AllowIncomplete` | `Tooltip_AllowIncomplete` |
| Partition list "Offset" header | `Column_Offset` | `Tooltip_ColumnOffset` |
