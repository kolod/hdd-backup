# UI Contract: Backup, Restore and Image Tabs

Supersedes the tab layout part of `specs/001-usb-sd-imaging/contracts/ui.md`. Menus, dialogs
(except the removed Shrink dialog) and the progress panel are unchanged. The approved mockups are
in [spec.md](../spec.md#layout-mockups-approved).

## Common structure (all three tabs)

```text
┌──────────────── left (fills) ────────────────┬─ right column (auto, clamped) ─┐
│                                              │ <Group heading>                │
│                                              │   options…                     │
├──────────────────────────────────────────────┴────────────────────────────────┤
│ <path caption>                                                                │
│ [ path …………………………………………………………………………………… ] [ Browse… ]                     │
│ (Restore only: image description, checksum files, size warning)               │
│                                                    [ action buttons, right ]  │
└───────────────────────────────────────────────────────────────────────────────┘
```

- Group headings: bold caption labels. Hidden rows collapse (no gaps).
- While an operation runs, all three tabs are disabled (`SetBusy`), as before.
- Tab order: Backup, Restore, Image. Keyboard tab order inside a tab: left area, right column,
  bottom area, action buttons.

## Backup tab

| Area | Controls (top to bottom) | Rules |
|------|--------------------------|-------|
| Left | "Source device:", device list (fills), "no devices" hint, unsuitable warning, "Back up anyway" `*`, partition list (single-partition scope only) | unchanged |
| Right | **Backup scope**: Whole device `*` / Single partition. **Format**: format list `*`, Fast compression `*`. **After backup**: Verify the image file after backup, Eject the device when finished | unchanged enabling and remembered settings |
| Bottom | "Save image as:", path + **Browse…**, **Start backup** | unchanged |

## Restore tab

| Area | Controls | Rules |
|------|----------|-------|
| Left | "Target device:", device list (fills; excludes the disk the program runs from), "no devices" hint, unsuitable warning, "Target partition:" + partition list (partition images only) | unchanged |
| Right | **After writing**: Read back and verify after writing, Eject the device when finished | unchanged |
| Bottom | "Image file:", shared image path + **Browse…**, image description, checksum files, size warning (see `ImageFit`), **Write to device…** | **Verify image** and **Shrink image…** removed |

## Image tab (new)

| Area | Controls | Rules |
|------|----------|-------|
| Left | "Image file:", shared image path + **Browse…**, image description, checksum files, **Shrink analysis** heading, analysis text (partitions, data used in the Linux partition, smallest possible image) or "Analyzing the image…", blockers in red | analysis shown for full-device images; for partition images a one-line reason replaces it |
| Right | **Shrink to**: (•) Smallest possible, ( ) Size of a device + device list (name and size), ( ) Card size + number + GiB/MiB. **Output format**: format list `*` (default Uncompressed) | shrink controls disabled unless the analysis is done and shrinkable; "Size of a device" disabled without devices |
| Bottom | "Save the smaller image as:", output path + **Browse…**, **Verify image**, **Create smaller image** | Verify: enabled with any loaded image. Create: see data-model `ShrinkTarget` validation |

**Create smaller image** asks to overwrite an existing output file (same text as today) and then
starts `OperationKind.ShrinkImage` with the shared progress panel. It never writes to a device.

## Shared image

- The Restore and Image tabs show one `SelectedImage`; typing or browsing on either tab updates
  both. Browse starts in the last image folder; choosing a file updates the last image folder.
- After **Verify image** saves checksum files, the image is reloaded on both tabs.
- Help → Device diagnostics uses the shared image when it is a full-device image (as it used the
  Restore tab image before).

## Tooltips

See the tooltip map in [data-model.md](../data-model.md#tooltip-map). Tooltips use the system
hover delay and are re-assigned on every language switch.

## Strings (all six languages)

| Change | Keys |
|--------|------|
| Shortened (same key) | `Backup_ScopeWhole`, `Format_Gzip`, `Format_Raw`, `Option_FastCompression`, `Option_AllowIncomplete`, `Column_Offset` |
| New tooltips | `Tooltip_ScopeWhole`, `Tooltip_FormatGzip`, `Tooltip_FormatRaw`, `Tooltip_FastCompression`, `Tooltip_AllowIncomplete`, `Tooltip_ColumnOffset` |
| New headings | `Group_BackupScope`, `Group_Format`, `Group_AfterBackup`, `Group_AfterWriting`, `Group_ShrinkTo`, `Group_OutputFormat`, `Image_AnalysisHeading` |
| New Image tab | `Tab_Image`, `Image_Analysis` ({0} partitions, {1} used, {2} smallest image), `Image_TargetDevice`, `Image_TargetCard`, `Image_Create`, `Image_Verify`, `Image_NotFullDevice` |
| New Restore warnings | `Restore_TooLarge`, `Restore_TooLargeShrink`, `Restore_TooLargeCannot` ({0} reasons) |
| Removed | `Restore_VerifyImage`, `Restore_Shrink`, `Shrink_Title`, `Shrink_Start`, `Shrink_TargetCard`, `Shrink_Summary`, and any other key left without a reference |
