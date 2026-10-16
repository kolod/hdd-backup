# Implementation Plan: Image Conversion and Checksum Calculation

**Branch**: `005-image-conversion` | **Date**: 2026-10-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/005-image-conversion/spec.md`

## Summary

Add conversion between `.img.gz` and `.img` and a direct "calculate checksums" action to the Image
tab. Conversion is a new engine operation (`ConvertImage`) built from the backup pipeline: read the
source image, check it against its stored checksums, write the output with sidecars and partition
metadata as partial files, verify by reading back, commit atomically. Checksum calculation reuses
image verification with a flag that saves only the missing sidecars after a clean pass, replacing
today's question after verification. The Image tab gets a "Same as the image" size choice (the
conversion), a fast compression option, and button labels that follow the state.

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: BCL (`GZipStream`, SHA-256) and Windows Forms; no new packages

**Storage**: image files and sidecars on local or removable disks; `HddBackup.ini` unchanged

**Testing**: xUnit in `tests/HddBackup.Core.Tests` (file-backed fixtures in `Fixtures/ext4`,
temporary folders), resource parity test, manual quickstart scenarios

**Target Platform**: Windows 7 SP1 – Windows 11, x86/x64

**Project Type**: desktop app (single WinForms executable; Core compiled in)

**Performance Goals**: conversion limited by compression and disk speed (SC-002); buffers and
pipeline as for backups

**Constraints**: never modify the source or its sidecars; no partial results; progress and
cancellation through the shared panel

**Scale/Scope**: one engine operation, one request flag, Image tab changes, about 10 strings × 6
languages

**Dependency**: PR #9 (`fix/gzip-progress`: `ImageHead`, `ImageDescriptor.ProgressSize`,
`ImageProgress`) must be merged and this branch rebased before implementation.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ No device access at all; the source image is only read | ✅ Partial files + atomic commit; source sidecars never written (R-3); errors stop with no result |
| II | Faithful Imaging | ✅ Only the two supported formats; content copied exactly | ✅ Source checked against stored checksums (R-2); output verified by read-back; gzip output standard (round trip with `gzip -dc` in quickstart) |
| III | Portable & Compact | ✅ BCL only | ✅ No new settings or files besides the requested image and sidecars |
| IV | Win 7 – 11 | ✅ File I/O and existing UI only | ✅ |
| V | Localized UI | ✅ New strings in six languages | ✅ String table in contracts/ui.md |
| VI | Test-Verified Disk I/O | ✅ Conversion lives in Core behind the existing reader/writer | ✅ Round-trip, mismatch, cancellation and sidecar tests planned |
| VII | Simplicity | ✅ Conversion between the two supported image formats is part of "raw or gzip images"; no new format | ✅ Reuses backup pipeline and verify path; no new operation for checksums |

No violations.

## Project Structure

### Documentation (this feature)

```text
specs/005-image-conversion/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/ui.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
src/HddBackup.Core/Imaging/
├── OperationRequest.cs      # ConvertImage kind, OutputPath, SaveMissingChecksums
├── OperationResult.cs       # OutputPath, SavedChecksumFiles
├── ImagingEngine.cs         # RunConvert, ValidateConvert, missing-sidecar save after verify
└── DestinationChecks.cs     # size-based FAT / free-space helper shared with backups

src/HddBackup.App/
├── Forms/ImageTab.cs        # Same-size choice, fast compression, labels, ConvertImage request
├── Forms/OperationPanel.cs  # convert phase label, result lines (converted file, saved checksums)
├── Forms/MainForm.cs        # remove OfferToSaveChecksums question; reload shared image after ops
└── Resources/Strings*.resx

tests/HddBackup.Core.Tests/Imaging/
├── ConvertImageTests.cs     # round trips, mismatch, cancel, sidecars, partition metadata
└── ChecksumCalculationTests.cs
```

**Structure Decision**: existing Core + App layout; all file logic in Core (tested), UI changes in
the Image tab and the operation panel.

## Complexity Tracking

No constitution violations.
