# Research: Two-Column Layout and an Image Tab

All items below were open questions in the Technical Context; each is resolved.

## R-1. Two-column layout that tolerates long translations

- **Decision**: each tab is a `TableLayoutPanel` with two rows (content, bottom area). The content
  row is a nested `TableLayoutPanel` with two columns: left `Percent 100`, right `AutoSize`. The
  right column is a top-down panel whose width is clamped to a range (about 220–300 px at 96 DPI,
  scaled with the form) and whose check boxes and radio buttons wrap their text within that width.
  The bottom area is the existing path row (`Ui.PathRow`) plus the right-to-left button flow.
- **Rationale**: the project already uses auto-sizing `TableLayoutPanel` / `FlowLayoutPanel`
  helpers (`Ui.cs`) for long German/French texts and Japanese fonts; the same approach keeps DPI
  scaling (`AutoScaleMode.Dpi`) working without manual pixel layout. Clamping the right column
  prevents a long translation from squeezing the device list.
- **Wrapping**: a `CheckBox` / `RadioButton` with `AutoSize = true` and `MaximumSize.Width` set
  wraps its text and grows in height. This is verified per language in the quickstart
  screenshots. Fallback if a control does not wrap on Windows 7: `AutoSize = false` with height
  from `TextRenderer.MeasureText(..., TextFormatFlags.WordBreak)` in a small `Ui` helper.
- **Alternatives considered**: `SplitContainer` (user-resizable splitter): adds a control and a
  setting to remember for no requested benefit. `GroupBox` frames for option groups: heavier
  visually and adds padding that costs width; a bold caption label is enough (FR-004).

## R-2. Window size

- **Decision**: default client size 980 × 680 at 96 DPI (was 820 × 680); minimum size 800 × 560
  (was 640 × 560).
- **Rationale**: device list columns (device 220, size 80, bus 50, volumes 80, table 100 = 530 px)
  plus the right column (≤ 300 px) plus padding fit in 980 px. The outer window (about 996 × 719)
  fits a 1024 × 768 screen with a taskbar (FR-021). Height is unchanged.
- **Alternatives considered**: keep 820 px and hide device list columns: loses information the
  safety principle asks for (bus type, partitions).

## R-3. Tooltips on controls, combo items and a list column header

- **Decision**:
  - One `ToolTip` component per tab; texts are (re)assigned in `ApplyStrings()` so a language
    switch updates them (FR-020).
  - Format lists: the tooltip of the combo box describes the **selected** item and is updated on
    `SelectedIndexChanged` (Windows combo boxes have no per-item tooltips).
  - "Offset" column of the partition list: `ListView` has no header tooltips in WinForms. A small
    `NativeWindow` attached to the list's header (`LVM_GETHEADER`) shows the shared `ToolTip`
    when the pointer is over the "Offset" header item (`HDM_GETITEMRECT`). Both messages exist
    since Windows 95, so Windows 7 is covered.
- **Rationale**: no third-party controls (Principle III); all APIs available on Windows 7
  (Principle IV).
- **Alternatives considered**: replacing `ListView` with `DataGridView` (has
  `ColumnHeader.ToolTipText`): larger change to a shared control used on two tabs, different
  look and selection behavior. Keeping "(bytes)" in the header: contradicts FR-019.

## R-4. Shared selected image and background analysis

- **Decision**: a new App-layer class `SelectedImage` owns the image path, the loaded
  `ImageDescriptor` (or the load error), the required device size, and the shrink analysis
  (`ShrinkPlan`) with its state. The Restore and Image tabs receive the same instance and
  subscribe to its `Changed` event. Analysis (`ShrinkPlanner.Analyze`) runs on a background task
  for full-device images only, with a `CancellationToken` that is cancelled when the path changes
  (edge case "image changed while analysis is running"). Results are marshalled to the UI thread.
- **Rationale**: one source of truth satisfies FR-016/FR-017 by construction; the analysis is
  computed once and reused by the Image tab (summary, shrink availability) and by the Restore tab
  size warning (FR-018). `MainForm` already reloads the restore image after saving checksum files;
  it now calls `SelectedImage.Reload()`.
- **Alternatives considered**: syncing two path text boxes through events: two copies of state,
  easy to get out of sync, analysis would run twice.

## R-5. Size warning on the Restore tab

- **Decision**: move the "does the image fit" decision that `MainForm.OfferShrinkIfTooLargeAsync`
  makes today into a pure Core function `ImageFit.Evaluate(fileSize, isRaw, requiredBytes, plan,
  deviceBytes)` returning `Fits`, `ShrinkToFit`, `CannotFit` or `Pending` (larger, analysis not
  finished). The Restore tab shows the warning text for `ShrinkToFit` / `CannotFit` / `Pending`;
  `MainForm` uses the same function before offering "Shrink and write", so the warning and the
  offer never disagree.
- **Rationale**: testable in `HddBackup.Core.Tests` (Principle VI); the existing rules (a raw
  image whose only overflow is unused space after the last partition still fits) are preserved.
- **Alternatives considered**: computing the warning in the tab: duplicates the rules and cannot
  be unit-tested (there is no App test project).

## R-6. Image tab replaces the Shrink dialog

- **Decision**: delete `ShrinkDialog`; move its logic into `ImageTab`. Target choices: smallest
  possible (`ShrinkTargetBytes = null`), size of a connected device (`DeviceInfo.Size`), card size
  (value × GiB/MiB). Output format list (compressed / uncompressed, default uncompressed) sets the
  output extension. The default output name moves from `ShrinkDialog.DefaultOutputPath` to Core
  (`ShrinkOutputName.Default(source, targetBytes, gzip)`) and is reused by the "Shrink and write"
  flow. The output path follows target and format changes until the user edits it or picks a
  file with **Browse…** (FR-014).
- **Device list for "Size of a device"**: all enumerated devices, including the one the program
  runs from (only the size is read). When the chosen device disappears on refresh, the choice
  falls back to "Smallest possible".
- **Rationale**: one place for shrinking; the engine request (`OperationKind.ShrinkImage`) is
  unchanged, so the shrink pipeline, free-space check and result verification are untouched
  (FR-011).
- **Alternatives considered**: keeping the dialog and opening it from the tab: two UIs for the
  same function.

## R-7. Strings

- **Decision**: short labels replace the old texts under the same keys where the meaning is the
  same (`Backup_ScopeWhole`, `Format_Gzip`, `Format_Raw`, `Option_FastCompression`,
  `Option_AllowIncomplete`, `Column_Offset`); explanations go to new `Tooltip_*` keys. New keys
  for the tab name, group headings, Image tab texts and the size warnings. Keys used only by the
  removed dialog or buttons are deleted. All six languages are updated together; the existing
  resource parity test enforces it.
- **Rationale**: Principle V; reusing keys keeps the diff small.
