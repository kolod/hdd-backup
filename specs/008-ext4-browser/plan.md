# Implementation Plan: Browse and Extract Files from an ext4 Partition

**Branch**: `008-ext4-browser` | **Date**: 2026-11-05 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/008-ext4-browser/spec.md`

## Summary

A fourth tab, **Files**, opens an uncompressed image or a USB/SD device read-only, lists its ext4
partitions and shows the chosen file system as a lazily expanded tree. Selecting a file shows its
beginning as text or a hex dump. **Extract…** copies a file or a directory to a Windows folder as a
regular imaging operation (preparation pass, progress, cancel, summary) so it reuses the progress
panel and the engine's cancellation and result model. Everything is built on the existing ext4
reader, partition-table reader, block-device abstraction and shared selected image; the only
changes to existing code are a shared file-system-state probe extracted from the shrink planner
and a new operation kind. The source is never written (no overlay, no journal replay: the on-disk
state is shown with a warning).

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: BCL and Windows Forms only; existing own ext4 reader; no new packages

**Storage**: images, devices (read-only) and the user-chosen destination folder; `HddBackup.ini`
unchanged

**Testing**: xUnit against the existing ext4 fixtures and file-backed fake devices; independent
comparison with `debugfs rdump` in the `ext4-check` CI job; manual hardware checklist step

**Target Platform**: Windows 7 SP1 – Windows 11, x86/x64

**Project Type**: desktop app (single WinForms executable; Core compiled in)

**Performance Goals**: root visible in under 5 s (SC-001); directory of 10 000 entries expanded or
file content shown in under 2 s (SC-002); extraction throughput limited by the source medium

**Constraints**: source opened read-only everywhere; no temporary files outside the destination
folder; no new runtime dependencies; executable stays under 5 MB

**Scale/Scope**: ~8 new Core classes (~900 lines), one tab and two small dialogs, one new operation
kind, ~45 strings × 6 languages, 3 new fixtures, constitution amendment 1.3.0

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ Source read-only; no write path to the device; no write confirmation needed because nothing is written to media | ✅ Destination guards (not the image, not a volume of the browsed device) reuse `FileIdentity` and `PathInfo.DiskNumbers`; partial files never look complete (R-5); a test fake asserts zero write calls (R-9) |
| II | Faithful Imaging | ✅ Images are not modified | ✅ Extracted bytes proven equal to `debugfs` output (SC-003) |
| III | Portable & Compact | ✅ No dependencies | ✅ No temp files outside the destination; extended-length paths are a path prefix, not a dependency |
| IV | Win 7 – 11 | ✅ Managed code and existing Win32 calls | ✅ `\\?\` paths (Win7-safe) instead of the Win10-only long-path opt-in (R-6); `TreeView`/`TextBox` only |
| V | Localized UI | ✅ New strings in six languages | ✅ String list in contracts/ui.md |
| VI | Test-Verified Disk I/O | ✅ Core logic behind `IBlockDevice` | ✅ Browser, preview, name mapping, planner, extractor tested on fake devices and fixtures; safety rules each have a test (R-9) |
| VII | Simplicity — scope | ❌ **Violation** under 1.2.0: scope excludes file-level backup | ✅ Resolved by amendment 1.3.0 (research R-8, applied 2026-11-05); design adds no write path and no second ext4 implementation |

The scope violation is resolved by a constitution amendment (MINOR) rather than a justified
deviation; see Complexity Tracking. The amendment text is drafted in research R-8 and is task T001.

## Project Structure

### Documentation (this feature)

```text
specs/008-ext4-browser/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/ui.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
.specify/memory/constitution.md                # amendment 1.3.0 (R-8)

src/HddBackup.Core/Ext4/
└── Ext4StateProbe.cs        # NEW: Clean / NeedsRepair / Damaged from the superblock (moved out of ShrinkPlanner)

src/HddBackup.Core/Browsing/                    # NEW
├── BrowseSource.cs          # open image (read-only) or device (OpenForRead); table; ext4 partitions
├── BrowsePartition.cs       # PartitionInfo + FileSystemState + unsupported reason
├── FsEntry.cs               # public DTO of one tree entry (no ext4 types leak)
├── Ext4FileSystemView.cs    # root, children, stat, symlink target, counts, ranged read; serialized access
├── FilePreview.cs           # bounded read, text/hex decision, formatting
├── WindowsNames.cs          # name sanitizing and collision-free mapping
├── ExtractionPlanner.cs     # walk, sizes, conflicts, per-item skips, free space
└── Ext4Extractor.cs         # copy with progress/cancel, partial-file handling, directory times, summary

src/HddBackup.Core/Shrinking/ShrinkPlanner.cs   # uses Ext4StateProbe
src/HddBackup.Core/Imaging/
├── OperationRequest.cs      # OperationKind.ExtractFiles; BrowseSource spec, entry path, destination, conflict policy
├── OperationResult.cs       # ExtractionSummary
└── ImagingEngine.cs         # Validate + RunExtract

src/HddBackup.App/Forms/
├── FilesTab.cs              # NEW: source, partition, tree, viewer, Extract…
├── ExtractConflictDialog.cs # NEW: replace / skip / cancel
├── ExtractSummaryDialog.cs  # NEW: skipped / renamed / failed list, copy to clipboard
├── MainForm.cs              # fourth tab; operation panel hookup
└── SelectedImage.cs         # unchanged API, shared with FilesTab
src/HddBackup.App/Resources/Strings*.resx        # ~45 strings × 6

tests/fixtures/ext4/make-browse-fixtures.sh      # NEW: odd names, deep tree, sparse/large, symlinks, special files, big directory
tests/fixtures/ext4/check-output.sh              # extended: compare extracted tree with `debugfs rdump`
tests/HddBackup.Core.Tests/Browsing/             # BrowseSourceTests, FileSystemViewTests, FilePreviewTests,
                                                 # WindowsNamesTests, ExtractionPlannerTests, ExtractorTests, ReadOnlyTests
docs/manual-hardware-checklist.md, README.md, docs/release-notes.md
```

**Structure Decision**: existing Core + App layout; browsing is a new Core namespace next to the
ext4 code; extraction is an operation of the existing engine so the UI-independent core stays
testable (Principle VI) and the progress panel is reused unchanged.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Principle VII scope (read-only browsing and extraction of files) | The main use case is backups of single-board computers; the user needs one file from a 16–32 GiB image without writing it to a card | Converting and mounting the image in Linux: needs another OS. Resolved by amendment 1.3.0, not by a standing deviation |
