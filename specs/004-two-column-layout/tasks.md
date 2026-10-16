---

description: "Task list for feature 004: two-column layout and an Image tab"
---

# Tasks: Two-Column Layout and an Image Tab

**Input**: Design documents from `/specs/004-two-column-layout/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md

**Tests**: Unit tests are included only for the new pure Core logic (`ShrinkOutputName`,
`ImageFit`), as required by constitution Principle VI. UI behavior is validated with the manual
scenarios and layout matrix in quickstart.md.

**Organization**: Tasks are grouped by user story (US1–US6 from spec.md).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to
- Paths: `src/HddBackup.Core/`, `src/HddBackup.App/`, `tests/HddBackup.Core.Tests/`
- Strings: every new or changed key goes into all six files
  `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx` in the same task

---

## Phase 1: Setup

**Purpose**: Confirm a green baseline before changing the UI

- [X] T001 Build `HddBackup.sln` in Release and run `tests/HddBackup.Core.Tests`; record the passing test count in `specs/004-two-column-layout/quickstart.md` under a "Baseline" line, and take Backup/Restore screenshots at default size (English, 100%) for the SC-001 row comparison

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Layout helpers, the shared image model and the moved output-name rule that several
stories use

- [X] T002 [P] Add `ShrinkOutputName.Default(string source, long? targetBytes, bool gzip)` in `src/HddBackup.Core/Shrinking/ShrinkOutputName.cs`, moving the body of `ShrinkDialog.DefaultOutputPath` unchanged (strip `.img.gz`/`.img`/`.gz`, suffix `_shrunk` or `_shrunk_<N.#>G`, invariant culture)
- [X] T003 [P] Add `tests/HddBackup.Core.Tests/Shrinking/ShrinkOutputNameTests.cs`: each source extension, no target, 8 GiB and 14.8 GB targets, gzip and raw outputs, path with directory
- [X] T004 Replace both callers of `ShrinkDialog.DefaultOutputPath` (`src/HddBackup.App/Forms/ShrinkDialog.cs`, `src/HddBackup.App/Forms/MainForm.cs` in `OfferShrinkIfTooLargeAsync`) with `ShrinkOutputName.Default` and delete the old method
- [X] T005 [P] Extend `src/HddBackup.App/Forms/Ui.cs` with: `TwoColumn(out TableLayoutPanel left, out TableLayoutPanel right)` returning a root `TableLayoutPanel` (row 0: content split into a `Percent 100` left column and an `AutoSize` right column; row 1: `AutoSize` bottom area returned via an `out` parameter or a third panel) per research R-1; `GroupCaption()` (bold caption label, top margin 10); `RightColumnWidth(Control owner)` clamping to 220–300 px scaled by the owner's DPI; and `Wrap(ButtonBase control, int width)` that sets `AutoSize = true` and `MaximumSize = new Size(width, 0)` so check-box and radio texts wrap
- [X] T006 Create `SelectedImage` in `src/HddBackup.App/Forms/SelectedImage.cs` per data-model.md: properties `Path`, `Descriptor`, `LoadError`, `RequiredDeviceBytes`, `Analysis`, `AnalysisState` (`None`/`Running`/`Done`/`Failed`), `AnalysisError`; `SetPath(string)` (ignore unchanged path; cancel the previous `CancellationTokenSource`; load with `ImageDescriptor.Load`, mapping `ImagingException` via `Formatters.Error` and IO/argument exceptions to `ex.Message` exactly as `RestoreTab.LoadImage` does; for full-device images compute `ShrinkPlanner.RequiredDeviceBytes` and start `ShrinkPlanner.Analyze` with `Task.Run`, discarding results whose token was cancelled); `Reload()` (re-read descriptor; keep a `Done` analysis when file length and last write time are unchanged); `Changed` event raised on the UI thread (capture `SynchronizationContext.Current` in the constructor); `IDisposable` cancelling any analysis
- [X] T007 Refactor `src/HddBackup.App/Forms/RestoreTab.cs` to take a `SelectedImage` in its constructor: the path box calls `SelectedImage.SetPath` on `TextChanged`, the tab updates its path text (without re-entering), image info and state on `Changed`; remove `_image`/`_imageError`/`LoadImage`; keep `ReloadImage()` delegating to `SelectedImage.Reload()`; `FullImagePath` reads from `SelectedImage`
- [X] T008 In `src/HddBackup.App/Forms/MainForm.cs`, create one `SelectedImage`, pass it to `RestoreTab`, dispose it in `Dispose`, and change the window to `ClientSize = new Size(980, 680)` and `MinimumSize = new Size(800, 560)` (research R-2)
- [X] T009 Build and run the app: Restore tab behaves exactly as before (image load, errors, checksum info, write, verify, shrink dialog); run all tests

**Checkpoint**: shared image model in place, no visible change except the window width.

---

## Phase 3: User Story 1 - Device on the left, options on the right (Priority: P1) 🎯 MVP

**Goal**: Backup and Restore tabs split into a left device area and a right options column.

**Independent Test**: open both tabs at the default size; device list left, all options in the
right column under group headings, device list taller than in the T001 screenshots.

- [X] T010 [P] [US1] Add keys `Group_BackupScope` ("Backup scope"), `Group_Format` ("Format"), `Group_AfterBackup` ("After backup"), `Group_AfterWriting` ("After writing") with translations to all six `src/HddBackup.App/Resources/Strings*.resx`
- [X] T011 [US1] Re-lay out `src/HddBackup.App/Forms/BackupTab.cs` with `Ui.TwoColumn`: left = "Source device:" caption, device list (fill), no-devices hint, unsuitable warning (`MaximumSize` width follows the left column instead of the fixed 760), "Back up anyway", partition list; right = `Group_BackupScope` + whole/partition radios, `Group_Format` + format combo (replace the `Backup_Format` caption) + fast compression, `Group_AfterBackup` + verify + eject, each option passed through `Ui.Wrap`; keep all event handlers, settings bindings and `UpdateState` rules unchanged
- [X] T012 [US1] Re-lay out `src/HddBackup.App/Forms/RestoreTab.cs` with `Ui.TwoColumn`: left = "Target device:" caption, device list (fill), no-devices hint, unsuitable warning, target partition caption + list; right = `Group_AfterWriting` + read-back verify + eject (wrapped); keep enabling rules unchanged
- [X] T013 [US1] Recompute the right column width and the `Ui.Wrap` widths in `ApplyStrings()` and on `DpiChanged`/`FontChanged` in `src/HddBackup.App/Forms/BackupTab.cs` and `src/HddBackup.App/Forms/RestoreTab.cs`, so a language switch re-wraps the texts
- [X] T014 [US1] Remove the `Backup_Format` key from all six `src/HddBackup.App/Resources/Strings*.resx` if no longer referenced; build; check scenarios 1–3 of quickstart.md

**Checkpoint**: US1 complete and demonstrable.

---

## Phase 4: User Story 2 - Image path at the bottom, start button and progress as now (Priority: P1)

**Goal**: full-width bottom area with the path, (Restore) image info, and the action buttons.

**Independent Test**: on both tabs the path row spans the full width above the right-aligned
start button; progress panel unchanged.

- [X] T015 [US2] In `src/HddBackup.App/Forms/BackupTab.cs`, place "Save image as:" caption, `Ui.PathRow(_path, _browse)` and the right-to-left action flow with **Start backup** in the bottom area of `Ui.TwoColumn`
- [X] T016 [US2] In `src/HddBackup.App/Forms/RestoreTab.cs`, place "Image file:" caption, path row, image info label (`MaximumSize` width follows the tab width), and the action flow (**Write to device…**, **Verify image**, **Shrink image…** for now) in the bottom area
- [X] T017 [US2] Verify tab order (left → right column → bottom → buttons) via `TabIndex` in both tabs; build; check scenarios 1 and 5 of quickstart.md

**Checkpoint**: Backup and Restore tabs match the approved mockups (except the Restore buttons,
removed in US3).

---

## Phase 5: User Story 3 - Image tab for work that does not touch a device (Priority: P1)

**Goal**: new Image tab with verify and shrink (smallest / size of a device / card size),
replacing the Shrink dialog.

**Independent Test**: create a smaller copy of the rpi-like fixture sized to a connected device,
then verify the original image, without any device write.

- [X] T018 [P] [US3] Add keys with translations to all six `src/HddBackup.App/Resources/Strings*.resx`: `Tab_Image` ("Image"), `Group_ShrinkTo` ("Shrink to"), `Group_OutputFormat` ("Output format"), `Image_AnalysisHeading` ("Shrink analysis"), `Image_Analysis` ("Partitions: {0}\nUsed in the Linux (ext4) partition: {1}\nSmallest possible image: {2}"), `Image_TargetDevice` ("Size of a device:"), `Image_TargetCard` ("Card size:"), `Image_Create` ("Create smaller image"), `Image_Verify` ("Verify image"), `Image_NotFullDevice` ("Only images of a whole device can be shrunk."); reuse `Shrink_TargetMinimum`, `Shrink_Output`, `Shrink_Analyzing`, `Unit_GiB`, `Unit_MiB`
- [X] T019 [US3] Create `src/HddBackup.App/Forms/ImageTab.cs` (`UserControl, ILocalizable`) with `Ui.TwoColumn`: left = "Image file:" caption, path row bound to the shared `SelectedImage` (same pattern as T007), image info (format, size, kind, checksum files — extract the text building from `RestoreTab.UpdateImageInfo` into a shared static helper, e.g. `ImageInfoText.For(SelectedImage)` in `src/HddBackup.App/Forms/ImageInfoText.cs`, and use it in both tabs), `Image_AnalysisHeading`, analysis label (`Shrink_Analyzing` while `Running`; `Image_Analysis` with partitions from `Partition_Summary`, used data and minimum, "—" when not measured, as `ShrinkDialog.OnShown` does; `Image_NotFullDevice` for partition images; `AnalysisError` when `Failed`), blockers label in dark red via `Formatters.Blockers`; right = `Group_ShrinkTo` + radios minimum / device (+ `ComboBox` of devices showing `Model` and `Formatters.Bytes(Size)`) / card (+ `NumericUpDown` 1…2 097 152 and GiB/MiB combo), `Group_OutputFormat` + format combo (`Format_Gzip`, `Format_Raw`; default Uncompressed); bottom = `Shrink_Output` caption, output path row, action flow with **Create smaller image** and **Verify image**
- [X] T020 [US3] Implement Image tab rules in `src/HddBackup.App/Forms/ImageTab.cs`: `SetDevices(IReadOnlyList<DeviceInfo>)` fills the device combo with all devices, disables the device radio when empty, and falls back to "Smallest possible" when the chosen device disappears; card size defaults to `ceil(minimum / GiB)` when the analysis finishes; output path pre-filled with `ShrinkOutputName.Default(source, target, gzip)` and updated on target/format/image change until the user edits it or uses Browse (track a `_outputEdited` flag); Browse uses `Dialog_ShrinkFilter`, the last image folder and sets the format from the chosen filter; Create enabled only per data-model `ShrinkTarget` validation, otherwise the reason (`Blocker_NotEnoughSpaceEvenAtMinimum`) shown in the blockers label; Create asks `Backup_ConfirmOverwrite` when the output exists and raises `StartRequested` with `OperationKind.ShrinkImage`, `ImagePath`, `ShrinkOutputPath`, `ShrinkTargetBytes`, `EjectAfter = false`; Verify raises `StartRequested` with `OperationKind.VerifyImage`; `OnEnabledChanged` re-applies the rules
- [X] T021 [US3] Wire the tab in `src/HddBackup.App/Forms/MainForm.cs`: `_imagePage` after Restore with text `Tab_Image`, construct `ImageTab(settings, selectedImage)`, subscribe `StartRequested` to `StartOperation`, call `SetDevices` in `RefreshDevices`, `ApplyStrings` in `ApplyStrings`, and disable it in `SetBusy`; remove `OpenShrinkDialog` and the `ShrinkRequested` subscription
- [X] T022 [US3] Remove **Verify image** and **Shrink image…** (fields, layout, `ShrinkRequested` event, `StartVerify`) from `src/HddBackup.App/Forms/RestoreTab.cs`
- [X] T023 [US3] Delete `src/HddBackup.App/Forms/ShrinkDialog.cs`; remove keys left without references (`Restore_VerifyImage`, `Restore_Shrink`, `Shrink_Title`, `Shrink_Start`, `Shrink_TargetCard`, `Shrink_Summary` — grep `src/` for each before removing) from all six `src/HddBackup.App/Resources/Strings*.resx`
- [X] T024 [US3] Build, run tests; check quickstart scenarios 7–10, 13 and 14

**Checkpoint**: Image tab works; Restore tab has only **Write to device…**.

---

## Phase 6: User Story 4 - One image shared by the Restore and Image tabs (Priority: P2)

**Goal**: one selected image on both tabs, reloaded after checksum files are saved.

**Independent Test**: select on Restore, see it on Image; change on Image, see it on Restore;
save checksums after Verify and see them on both.

- [X] T025 [US4] Ensure both tabs update their path box from `SelectedImage.Changed` without feedback loops (compare text before assigning; suppress `TextChanged` handling while assigning) in `src/HddBackup.App/Forms/RestoreTab.cs` and `src/HddBackup.App/Forms/ImageTab.cs`; Browse on either tab updates `PortableSettings.LastImageFolder`
- [X] T026 [US4] In `src/HddBackup.App/Forms/MainForm.cs`, `OfferToSaveChecksums` calls `SelectedImage.Reload()` (instead of `_restore.ReloadImage()`), and the Device diagnostics dialog takes the shared image path when it is a full-device image
- [X] T027 [US4] Check quickstart scenarios 6 and 11

**Checkpoint**: SC-007 met.

---

## Phase 7: User Story 5 - Early warning when the image is larger than the device (Priority: P2)

**Goal**: Restore tab warns as soon as the selected image does not fit the selected device;
the warning and the "Shrink and write" offer use one rule.

**Independent Test**: 16 GB image + 8 GB device shows the warning; a 32 GB device hides it.

- [X] T028 [P] [US5] Add `ImageFit` in `src/HddBackup.Core/Shrinking/ImageFit.cs`: `enum ImageFitResult { Fits, Pending, ShrinkToFit, CannotFit }` and `static ImageFitResult Evaluate(bool isRaw, long fileSize, long requiredBytes, ShrinkPlan plan, bool analysisFailed, long deviceBytes)` implementing the rules now in `MainForm.OfferShrinkIfTooLargeAsync` (raw: fits when `fileSize <= device`, or when `requiredBytes > 0 && requiredBytes <= device`; gzip: fits when `requiredBytes <= device`) and the data-model table for the other results
- [X] T029 [P] [US5] Add `tests/HddBackup.Core.Tests/Shrinking/ImageFitTests.cs`: raw fits by file size; raw larger but partitions fit; gzip fits; larger with null plan → `Pending`; larger with failed analysis → `CannotFit`; shrinkable plan fitting → `ShrinkToFit`; minimum above device → `CannotFit`; plan with blockers → `CannotFit`
- [X] T030 [P] [US5] Add keys with translations to all six `src/HddBackup.App/Resources/Strings*.resx`: `Restore_TooLarge` ("The image is larger than the device."), `Restore_TooLargeShrink` ("The image is larger than the device. It will be shrunk to fit before writing."), `Restore_TooLargeCannot` ("The image is larger than the device and cannot be shrunk to fit: {0}")
- [X] T031 [US5] In `src/HddBackup.App/Forms/RestoreTab.cs`, add a dark-red size warning label under the image info in the bottom area; on `SelectedImage.Changed` and on device selection change, evaluate `ImageFit` for full-device images with a selected device and show `Restore_TooLarge` / `Restore_TooLargeShrink` / `Restore_TooLargeCannot` (reasons via `Formatters.Blockers` or `Blocker_NotEnoughSpaceEvenAtMinimum`); hide it for `Fits`, partition images, or no device; do not change the enabling of **Write to device…**
- [X] T032 [US5] Refactor `OfferShrinkIfTooLargeAsync` in `src/HddBackup.App/Forms/MainForm.cs` to use `ImageFit.Evaluate`, taking the plan from `SelectedImage` when it is for the same path and `Done`, otherwise analyzing as today (wait cursor); keep the offer, confirmation and two-step run unchanged
- [X] T033 [US5] Build, run tests; check quickstart scenario 12

**Checkpoint**: warning and offer agree in all cases.

---

## Phase 8: User Story 6 - Short labels with explanations in tooltips (Priority: P3)

**Goal**: six labels shortened, explanations in tooltips, in every language.

**Independent Test**: hover each control in the tooltip map in all six languages.

- [X] T034 [P] [US6] In all six `src/HddBackup.App/Resources/Strings*.resx`, shorten `Backup_ScopeWhole`, `Format_Gzip`, `Format_Raw`, `Option_FastCompression`, `Option_AllowIncomplete`, `Column_Offset` to the text before the parenthesis, and add `Tooltip_ScopeWhole`, `Tooltip_FormatGzip`, `Tooltip_FormatRaw`, `Tooltip_FastCompression`, `Tooltip_AllowIncomplete`, `Tooltip_ColumnOffset` with the former explanations (English per the spec's Tooltips table; translate from the existing parenthesized text in each language)
- [X] T035 [US6] Add a `ToolTip` to `src/HddBackup.App/Forms/BackupTab.cs` and assign tooltips in `ApplyStrings()` for whole device, fast compression and back up anyway; set the format combo tooltip from the selected item in `OnFormatChanged` and `ApplyStrings`
- [X] T036 [P] [US6] Add a `ToolTip` to `src/HddBackup.App/Forms/ImageTab.cs` for the output format combo (selected item), same pattern as T035
- [X] T037 [P] [US6] Create `src/HddBackup.App/Forms/HeaderToolTip.cs`: a `NativeWindow` assigned to the header handle from `SendMessage(listView.Handle, LVM_GETHEADER = 0x101F, 0, 0)`; on `WM_MOUSEMOVE` it finds the header item under the pointer with `HDM_GETITEMRECT = 0x1207` and shows/hides a `ToolTip` for the configured column index; on `WM_MOUSELEAVE` it hides it (track with `TrackMouseEvent`); P/Invoke declarations in the same file or `src/HddBackup.Core/Native/NativeMethods.cs` style; re-attach when the list handle is recreated
- [X] T038 [US6] Use `HeaderToolTip` in `src/HddBackup.App/Forms/PartitionGrid.cs` for the Offset column with text `Tooltip_ColumnOffset`, updated in `ApplyStrings()`
- [X] T039 [US6] Build; check quickstart scenario 4 in all six languages

**Checkpoint**: all user stories complete.

---

## Phase 9: Polish & Cross-Cutting Concerns

- [X] T040 Run the full test suite and the resource parity test; grep `src/HddBackup.App` for string keys passed to `Strings.Get`/`Strings.Format` and confirm each exists in all six `.resx` files and no key in `Strings.resx` is unreferenced (except dynamic `Warning_*`, `Error_*`, `Blocker_*`, `Phase_*` families)
- [ ] T041 Execute the layout matrix in `specs/004-two-column-layout/quickstart.md` (six languages at 100% and 150% on Windows 11; German and Japanese on Windows 7; minimum window size; 1024×768) and record the results; fix clipping found, falling back to the measured-height wrap helper from research R-1 if a control does not wrap on Windows 7
- [X] T042 [P] Update `specs/001-usb-sd-imaging/contracts/ui.md` with a note that the tab layout is superseded by `specs/004-two-column-layout/contracts/ui.md`, and update `README.md` (shrinking now on the **Image** tab: "Size of a device", "Card size", "Smallest possible"; remove the "Shrink image… on the Restore tab" wording)
- [X] T043 [P] Add a feature 004 entry to `docs/release-notes.md` (two-column tabs, Image tab, size warning, tooltips) and update `docs/manual-hardware-checklist.md` steps that mention the Shrink dialog or the Restore tab Verify/Shrink buttons
- [ ] T044 Run quickstart scenarios 1–14 end to end on real media and record the date and result in `specs/004-two-column-layout/quickstart.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)** → **Foundational (Phase 2)** → user stories.
- **US1 (Phase 3)** and **US2 (Phase 4)** edit the same two files; do US1 then US2.
- **US3 (Phase 5)** depends on Phase 2 (`SelectedImage`, `ShrinkOutputName`, `Ui.TwoColumn`) and
  on US2 only for T022 (removing buttons from the new Restore bottom area).
- **US4 (Phase 6)** depends on US3 (the Image tab exists).
- **US5 (Phase 7)** depends on Phase 2 and US2 (bottom area); T028–T030 can start any time.
- **US6 (Phase 8)** depends on US1 (Backup layout) and US3 (Image tab) for T035/T036; T034 and
  T037 can start any time after Phase 2.
- **Polish (Phase 9)** after all stories.

### Within Each Story

- String tasks before the UI tasks that use the keys.
- Core function and its tests (T002/T003, T028/T029) before their callers.

### Parallel Opportunities

- Phase 2: T002, T003, T005 in parallel; then T006 → T007 → T008.
- T010, T018, T030, T034 (resource edits) touch the same `.resx` files: run them one after
  another, but each in parallel with code tasks in other files.
- US5 Core work (T028, T029) in parallel with US3 UI work.
- T037 (`HeaderToolTip`) in parallel with any other story.
- Polish: T042 and T043 in parallel.

## Parallel Example: User Story 5

```text
Task: "T028 Add ImageFit in src/HddBackup.Core/Shrinking/ImageFit.cs"
Task: "T029 Add ImageFitTests in tests/HddBackup.Core.Tests/Shrinking/ImageFitTests.cs"
```

## Parallel Example: User Story 6

```text
Task: "T036 Format combo tooltip in src/HddBackup.App/Forms/ImageTab.cs"
Task: "T037 HeaderToolTip in src/HddBackup.App/Forms/HeaderToolTip.cs"
```

## Implementation Strategy

### MVP First

1. Phases 1–2 (baseline, shared model, helpers).
2. Phase 3 (US1): two-column Backup and Restore tabs.
3. Stop and validate scenarios 1–3; this already delivers the main request.

### Incremental Delivery

1. US1 + US2 → approved Backup/Restore layout.
2. US3 → Image tab, Shrink dialog gone.
3. US4 → shared image polish.
4. US5 → early size warning.
5. US6 → tooltips.
6. Polish → layout matrix, docs, hardware run.
