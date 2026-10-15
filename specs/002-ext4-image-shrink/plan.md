# Implementation Plan: Shrink ext4 Image to Fit a Smaller Card

**Branch**: `002-ext4-image-shrink` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-ext4-image-shrink/spec.md`

## Summary

Make two-partition SBC images (FAT32 boot plus ext4 root) fit smaller cards, on Windows, without
Linux. The ext4 root file system is **rebuilt** at a smaller size: an own managed ext4 reader
reads the source, and an own ext4 writer creates a new file system with the same features, UUID,
label and hash seed and copies every inode into it. The boot partition and everything before the
root partition are copied byte-for-byte, and only the last partition entry's length changes. The
result is checked structurally and compared object by object with the source before success is
reported. The feature is available as a standalone "Shrink image" action and as "Shrink and
write" when an image is too large for the target card.

## Technical Context

**Language/Version**: C# 7.3 on .NET Framework 4.8 (unchanged, see feature 001)

**Primary Dependencies**: BCL only. ext4 reader, writer, checker and crc32c are implemented in
`HddBackup.Core` (no GPL code, no native libraries).

**Storage**: source image (read-only), optional working copy (`*.work.partial`) for gzip
sources, output image `*.partial` → final name plus checksum sidecars, all in the user-chosen
folder.

**Testing**: xUnit (net48) with committed ext4 fixtures. Developer-only Docker scripts
(`e2fsck -fn`, tree diff) give an independent check; manual boot test on a Raspberry Pi.

**Target Platform**: Windows 7 SP1 – 11, x86/x64, in the existing single `HddBackup.exe`.

**Project Type**: desktop-app (extension of feature 001).

**Performance Goals**: 16 GB image with 3 GB used → shrunk in ≤ 15 min on an SSD (SC-004);
dominated by copying file data sequentially.

**Constraints**: the original image is never modified. Executable stays < 5 MB. Memory
< 300 MB: bitmaps for 64 GB at 4 KiB blocks take 2 MB, and the inode map is streamed per group.

**Scale/Scope**: file systems up to 64 GiB, up to about 2 M inodes; block sizes 1 K/2 K/4 K.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Gate | Pre-design | Post-design |
|---|-----------|------|------------|-------------|
| I | Data Safety First | Original never modified; refuse unsafe sources; verify before success; no misleading partial output; device write keeps all 001 guards | ✅ FR-004–FR-008 | ✅ rebuild never writes to source; 3-step verification (R-6); `.partial` naming; "shrink and write" reuses `RestoreGuard` + confirm dialog |
| II | Faithful Imaging | Boot partition and bytes before root identical; files and metadata identical | ✅ FR-009, FR-010 | ✅ byte copy of prefix; tree comparison covers content, metadata, xattrs, links (R-6) |
| III | Portable & Compact | No new runtime deps; single exe; files only in chosen folder | ✅ BCL only | ✅ own reader/writer (R-1); working copy next to output (R-5) |
| IV | Win7–Win11 | No new OS APIs | ✅ pure managed file I/O | ✅ managed crc32c (R-7) |
| V | English-first, localized UI | New texts in 6 languages | ✅ FR-014 | ✅ keys listed in [contracts/ui.md](contracts/ui.md) |
| VI | Test-Verified Disk I/O | Fixtures, round trip, negative cases | ✅ | ✅ fixtures from real mke2fs; own checker + independent e2fsck in dev (R-8) |
| VII | Simplicity | Scope limited to shrinking the last ext4 partition | ✅ growing, other FS, on-device shrink out of scope | ✅ linear directories (no htree writer), sequential allocator |

**Scope note (Principle VII)**: the constitution limits scope to imaging. Image shrinking was
requested explicitly as part of imaging (making images fit cards), so this plan records it as an
extension of the imaging scope. **A constitution amendment (MINOR, v1.1.0) is recommended** to
list "shrinking the last ext4 partition of an image" explicitly. This is tracked as task T001 of
this feature.

**Result**: PASS with the amendment noted above.

## Project Structure

### Documentation (this feature)

```text
specs/002-ext4-image-shrink/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── core-api.md       # ShrinkPlanner, ImageShrinker, Ext4 reader/writer contracts
│   └── ui.md             # Shrink dialog, "shrink and write" offer, strings
└── tasks.md              # /speckit-tasks
```

### Source Code (repository root)

```text
src/HddBackup.Core/
├── Ext4/
│   ├── Crc32c.cs               # Castagnoli, table-driven
│   ├── Ext4Structures.cs       # superblock, group descriptor, inode, extent, dirent, xattr layouts
│   ├── Ext4Features.cs         # feature flags, supported-set validation
│   ├── Ext4Reader.cs           # read-only FS over IBlockDevice: inodes, extents/block maps, dirs, xattrs, symlinks
│   ├── Ext4Layout.cs           # mke2fs-like geometry for a target size (groups, flex_bg, backups, reserved GDT)
│   ├── Ext4Writer.cs           # builds new FS: allocator, inode/extent/dir/xattr writers, journal, resize inode
│   ├── Ext4Checker.cs          # structural consistency check (FR-007)
│   └── Ext4TreeComparer.cs     # source ↔ output comparison
├── Shrinking/
│   ├── ShrinkPlan.cs           # analysis result + blocking reasons
│   ├── ShrinkPlanner.cs        # layout analysis, used size, minimum size (R-4)
│   └── ImageShrinker.cs        # orchestration: working copy, prefix copy, rebuild, MBR/GPT update, verify, sidecars
├── Devices/FileBlockDevice.cs  # IBlockDevice over a file (read-only or read/write)
└── Imaging/ImagingEngine.cs    # + OperationKind.ShrinkImage
src/HddBackup.App/
├── Forms/ShrinkDialog.cs       # standalone shrink: target size / minimum, output path
├── Forms/RestoreTab.cs         # "Shrink image…" button; "shrink and write" offer
└── Resources/Strings*.resx     # new keys
tests/HddBackup.Core.Tests/
├── Ext4/                       # crc32c, reader vs fixtures, layout, writer round trip, checker negatives
├── Shrinking/                  # planner, shrinker end-to-end on fixtures, refusals, cancellation
└── Fixtures/ext4/*.img.gz      # generated by tests/fixtures/ext4/make-fixtures.sh (Docker, dev only)
tests/fixtures/ext4/
├── make-fixtures.sh
└── check-output.sh
```

**Structure Decision**: new `Ext4` and `Shrinking` folders in the existing core library, with
the same single-exe linking as feature 001.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Own ext4 writer (large component) | No managed, non-GPL ext4 writer exists; Linux tools are excluded by the user | Porting resize2fs: GPL-2 and higher data-loss risk (R-1) |
| Working copy for gzip sources (temporary file) | ext4 reading needs random access; gzip has none | Streaming would need the whole FS in memory (GBs) |
