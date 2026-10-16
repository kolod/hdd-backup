# Implementation Plan: Two-Column Layout and an Image Tab

**Branch**: `004-two-column-layout` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/004-two-column-layout/spec.md`

## Summary

Rearrange the Backup and Restore tabs into a device area on the left, an options column on the
right and a full-width bottom area (path, start button), as in the approved mockups. Move
"verify image" and "shrink image" into a new **Image** tab that replaces the Shrink dialog and adds
"shrink to the size of a connected device". The Restore and Image tabs share one selected image,
whose shrink analysis also drives an early "image is larger than the device" warning on the
Restore tab. Parenthesized explanations in six labels move into tooltips. The engine, the write
paths and the progress panel are unchanged; two small pure functions move into Core so they can be
tested.

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: BCL and Windows Forms only (no new packages)

**Storage**: `HddBackup.ini` next to the executable; no new settings

**Testing**: xUnit (`tests/HddBackup.Core.Tests`), resource parity test, manual layout matrix
(quickstart)

**Target Platform**: Windows 7 SP1 – Windows 11, x86/x64, DPI-aware

**Project Type**: desktop app (single WinForms executable; Core sources compiled in)

**Performance Goals**: UI stays responsive while an image is analyzed (analysis on a background
task, as today in the Shrink dialog)

**Constraints**: no clipping in six languages at 100–200% scaling; window fits 1024 × 768;
executable stays well under 5 MB

**Scale/Scope**: 3 tabs, about 30 changed or new strings × 6 languages, one dialog removed

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ No change to device lists, confirmation, locking or size refusal. The Image tab never writes to a device. "Shrink and write" keeps its confirmation | ✅ `ImageFit` makes the warning and the offer use one rule; Restore target list still excludes the program's own disk; the "Size of a device" list only reads sizes |
| II | Faithful Imaging | ✅ Engine untouched | ✅ Same `OperationRequest`s for the same choices (FR-023) |
| III | Portable & Compact | ✅ No dependencies, no new settings files | ✅ Header tooltip via `NativeWindow` + two Win32 messages, no third-party controls |
| IV | Win 7 – 11 | ✅ WinForms layout only | ✅ `LVM_GETHEADER` / `HDM_GETITEMRECT` exist on Windows 7; wrapping checked on Windows 7 in quickstart |
| V | Localized UI | ✅ All new text in six languages | ✅ String table in contracts/ui.md; parity test; long-translation and CJK checks in the layout matrix |
| VI | Test-Verified Disk I/O | ✅ No disk I/O changes | ✅ New Core logic (`ImageFit`, `ShrinkOutputName`) gets unit tests; existing shrink and write tests unchanged |
| VII | Simplicity | ✅ Within scope (UI for existing functions) | ✅ One dialog removed, one shared model instead of synced copies; no splitter or new settings |

No violations; Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/004-two-column-layout/
├── plan.md              # This file
├── research.md          # Phase 0: layout, window size, tooltips, shared image, ImageFit, Image tab, strings
├── data-model.md        # Phase 1: SelectedImage, ShrinkTarget, ShrinkOutputName, ImageFit, tooltip map
├── quickstart.md        # Phase 1: automated checks, manual scenarios, layout matrix
├── contracts/
│   └── ui.md            # Phase 1: tab layouts, rules, string changes
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
src/HddBackup.Core/
├── Shrinking/
│   ├── ImageFit.cs              # NEW: Fits / Pending / ShrinkToFit / CannotFit (from MainForm rules)
│   └── ShrinkOutputName.cs      # NEW: default output name (moved from ShrinkDialog)

src/HddBackup.App/
├── Forms/
│   ├── Ui.cs                    # + TwoColumn(), GroupCaption(), wrap helper
│   ├── BackupTab.cs             # re-laid out; tooltips
│   ├── RestoreTab.cs            # re-laid out; uses SelectedImage; size warning; Verify/Shrink removed
│   ├── ImageTab.cs              # NEW: verify + shrink (replaces ShrinkDialog)
│   ├── ShrinkDialog.cs          # DELETED
│   ├── PartitionGrid.cs         # "Offset" header tooltip
│   ├── HeaderToolTip.cs         # NEW: NativeWindow on the ListView header
│   ├── MainForm.cs              # third tab, SelectedImage, window size, ImageFit in shrink offer
│   └── SelectedImage.cs         # NEW: shared image + background analysis
└── Resources/Strings*.resx      # 6 languages

tests/HddBackup.Core.Tests/
└── Shrinking/
    ├── ImageFitTests.cs         # NEW
    └── ShrinkOutputNameTests.cs # NEW
```

**Structure Decision**: existing two-project layout (Core + App, Core compiled into the
executable). Pure decisions go to Core for tests; everything else stays in App forms.
`SelectedImage` lives in the App project (`Forms/`) because it raises UI-thread events.

## Complexity Tracking

No constitution violations.
