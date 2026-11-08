# Implementation Plan: Editing Files of an ext4 Partition (Edit Mode, Diff File, Save)

**Branch**: `010-ext4-writer` | **Date**: 2026-11-08 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/010-ext4-writer/spec.md`

## Summary

The Files tab (features 008, 009) gets an **Edit** mode. For an **image**, every change (edit text,
create, delete, rename) is kept in a diff file next to the image and shown through a
`PendingTree : IFileTree` that lays the changes over the on-disk tree; **Save** computes, in memory,
the ordered set of block writes that an in-place ext4 editor would make, writes that set to a redo
log, replays it into the image, deletes the diff and redo files and updates or creates the `.sha256`
file. An interrupted save is finished by replaying the redo log. For a **card**, edit mode asks once;
every change is turned into the same ordered write set and replayed at once into the ext4
partition through a range-limited, lock-free write handle; no diff file, no redo file, no checksum.
The editor is own managed code that modifies one file system in place (allocation, inodes,
directories, extent trees, all checksums and counters) and is checked by the product's own checker
and, independently, by `e2fsck -fn` in CI.

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: BCL and Windows Forms only; feature 008's `IFileTree`, `BrowseSource`,
`Ext4Reader`, `PartitionView`; feature 006's `Ext4Checker`, `Crc32c`; feature 005's `ChecksumFile`;
no new packages, no GPL code

**Storage**: the diff file `<image>.hddbackup-diff` and, only during a save, the redo log
`<image>.hddbackup-redo`, both beside the image; nothing for a card

**Testing**: xUnit; real ext4 fixtures from Docker (`make-editing-fixtures.sh`), in-memory devices,
a fault-injecting device for interruption tests at every write; independent oracle in the
`ext4-check` job: `e2fsck -fn` over every saved image the tests export and a `debugfs rdump` tree
comparison with the expected tree

**Target Platform**: Windows 7 SP1 – Windows 11, x86/x64

**Project Type**: desktop app (single WinForms executable; Core compiled in)

**Performance Goals**: writing the changes of a save ≤ 30 s for any image size (the write set is
small: a few hundred blocks plus the file data); one change on a card ≤ 5 s; the checksum rereads
the whole image and takes as long as verifying it (progress, cancel)

**Constraints**: nothing is written before the first confirmed Save (image) or the edit-mode
confirmation (card); writes only inside the edited partition, enforced by `PartitionView`; no
dismount or lock of any volume; a file system that is not clean, damaged or using an unsupported
feature is never edited; executable stays under 5 MB

**Scale/Scope**: ~12 Core classes (~3 000 lines), 3 dialogs/controls, ~120 strings × 6 languages,
~8 new fixtures, constitution amended to 1.5.0 (done in this plan)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ after amendment 1.5.0 (applied 2026-11-08): removable media only, confirmation naming device and partition, range-limited writes, refuse unclean/damaged file systems, fit check, offset on error, completable save | ✅ `PartitionView` rejects any write outside the partition (tested with a recording device); one ordered write set per change gives "before / after / repairable" on interruption (R-5); the redo log makes an image save completable (R-8); preflight read-back verification of the write set before the first real write (R-7) |
| II | Faithful Imaging | ✅ Backup/restore untouched | ✅ Editing is a separate mode; images are not modified until Save; the partition table and other partitions are never written (SC-006) |
| III | Portable & Compact | ✅ No dependencies | ✅ Diff and redo files only beside the image; a card leaves nothing on the host; no registry, no new resources except strings |
| IV | Win 7 – 11 | ✅ Managed code and existing P/Invoke | ✅ One new device-open mode (`PhysicalDiskDevice.Open(writable: true)` without volume locks) on the same API; to be checked by hand on Windows 7 (checklist) |
| V | Localized UI | ✅ New strings in six languages | ✅ Codes and arguments from Core, texts from resources; parity test extended (contracts/ui.md) |
| VI | Test-Verified Disk I/O | ✅ Writer logic behind `IBlockDevice` | ✅ Fixtures + own checker + `e2fsck -fn` oracle; interruption injected at every write; read-only/zero-write proofs for browse mode; recording device for card range |
| VII | Simplicity | ✅ Amendment 1.5.0 covers editing | ✅ No resize, no journal use, no move across directories, no special files, no xattr editing; directories modified are rewritten linearly instead of maintaining htrees |

No violation; Complexity Tracking lists the one new device access method.

## Project Structure

### Documentation (this feature)

```text
specs/010-ext4-writer/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/ui.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
src/HddBackup.Core/Editing/                       # NEW
├── Ext4EditSupport.cs      # can this file system be edited? (features, state) → reason codes
├── WriteSet.cs             # ordered (offset, bytes) list; RecordingBlockDevice (reads see own writes)
├── Ext4Allocator.cs        # block and inode bitmaps, uninit groups, group descriptors, counters, checksums
├── Ext4DirEditor.cs        # add/remove/rename entries, linear rewrite of indexed directories, dirent tails
├── Ext4Editor.cs           # facade: ReplaceContent, CreateFile, CreateDirectory, Delete, Rename → writes
├── EditChange.cs           # one change (kind, path, new path, content, expected original)
├── DiffFile.cs             # the pending-changes file: read, write atomically, identity of the image
├── RedoLog.cs              # the write set of a save, with progress marker, for completing a save
├── PendingTree.cs          # IFileTree = on-disk tree + changes (new/deleted/edited), for tree and viewer
├── ImageEditSession.cs     # edit mode of an image: changes, diff file, Save, Finish save, Discard
├── CardEditSession.cs      # edit mode of a device: confirm, apply one change at once
└── SaveChecksums.cs        # recalculate or create the .sha256 beside the image

src/HddBackup.Core/Devices/                       # IDeviceAccess.OpenForPartitionWrite (no volume locks)
src/HddBackup.Core/Imaging/                       # OperationKind.SaveEdits, request fields, RunSaveEdits
src/HddBackup.App/Forms/
├── FilesTab.cs             # Edit / Save / Discard buttons, editor mode of the viewer, tree context menu
├── EditCardDialog.cs       # NEW: confirmation when switching edit mode on for a card
├── SaveChangesDialog.cs    # NEW: list of changes before Save (images)
└── EditTexts.cs            # NEW: reason codes and change kinds → localized text
src/HddBackup.App/Resources/Strings*.resx         # ~120 strings × 6

tests/HddBackup.Core.Tests/Editing/               # allocator, directory, editor, diff, redo, pending tree,
                                                  # image session, card session, interruption, checker/oracle exports
tests/fixtures/ext4/make-editing-fixtures.sh      # real images: htree directory, hard links, xattrs, nearly full,
                                                  # 1 KiB blocks, no metadata_csum, rpi-like (FAT + ext4)
tests/fixtures/ext4/check-editing.sh              # e2fsck -fn and debugfs tree comparison of saved images
tools/ci/Invoke-Ext4Checks.sh                     # calls check-editing.sh for exported saved images
docs/manual-hardware-checklist.md, README.md, docs/release-notes.md, docs/safety-review.md
```

**Structure Decision**: a new Core namespace `Editing` next to `Browsing`; the editor reuses the
reader's structure parsers and the checker's CRC code but never the shrink writer (which builds a
new file system). One ordered **write set** is the single currency: the editor produces it against
a recording overlay, the preflight verifies it, and either the image session (via the redo log) or
the card session replays it. The App only asks and displays.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| New `IDeviceAccess.OpenForPartitionWrite` (write handle without locking or dismounting volumes) | The edited partition is not a Windows volume, and dismounting the card's FAT boot volume would be needless and risky; `OpenExclusiveForWrite` locks and dismounts all of them | Reusing `OpenExclusiveForWrite` dismounts volumes the user did not ask to touch and fails when the FAT volume is in use |
