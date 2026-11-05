# Data Model: Browse and Extract Files from an ext4 Partition

All types are in `HddBackup.Core.Browsing` unless noted and are public DTOs; ext4 internals stay
internal (research R-1).

## BrowseSource

| Field | Type | Notes |
|-------|------|-------|
| `Kind` | enum `Image`, `Device` | |
| `ImagePath` | string | for `Image`; uncompressed images only |
| `Device` | `DeviceInfo` | for `Device`; from the Back up tab's device list |
| `Partitions` | list of `BrowsePartition` | every partition found; ext4 ones are browsable |
| `TableStyle` | `TableStyle` | existing enum; `None` for a single-partition image |

Rules: opened read-only; `.img.gz` is rejected with a `NeedsConversion` reason, never opened; a
source with no ext4 partition yields an empty browsable set and the list of partitions present.

## BrowsePartition

| Field | Type | Notes |
|-------|------|-------|
| `Number`, `Name`, `Offset`, `Length`, `FileSystem` | from `PartitionInfo` | |
| `IsBrowsable` | bool | ext4, superblock valid, supported features |
| `Unsupported` | string | reason when not browsable (feature name, "not ext4", "damaged superblock") |
| `State` | `FileSystemState` | existing type from feature 006: `Clean`, `NeedsRepair` + reasons, `Damaged` + problems |

## FsEntry

| Field | Type | Notes |
|-------|------|-------|
| `Inode` | uint | identity inside the view |
| `Name` | string | decoded UTF-8, invalid bytes as U+FFFD |
| `Path` | string | `/etc/hostname`, built from names |
| `Kind` | enum `Directory`, `File`, `Symlink`, `Special` (+ `SpecialKind`: char, block, fifo, socket) | |
| `Size` | long | bytes; 0 for directories and special files |
| `Mode` | ushort | shown as `-rw-r--r--` |
| `Uid`, `Gid` | uint | numbers |
| `Modified` | DateTime (UTC) | |
| `LinkTarget` | string | symlinks only |
| `Unreadable` | string | reason; `null` when readable |
| `NotViewable` | string | reason (encrypted, unsupported storage); `null` otherwise |

## Ext4FileSystemView (facade)

| Member | Notes |
|--------|-------|
| `Root` | `FsEntry` of `/` |
| `GetChildren(entry, ct)` | sorted (directories first); entries that cannot be read are returned marked `Unreadable` |
| `GetEntry(path)` | for extraction requests |
| `Counts` | `FileSystemCounts { Files, Directories, Approximate }` from group descriptors (R-4) |
| `ReadRange(entry, offset, count)` | holes read as zeros; fails with a typed error for unreadable entries |
| `State` | the partition's `FileSystemState` |

All calls take one internal lock; the view is disposed with the tab or on source change.

## FilePreview

| Field | Type | Notes |
|-------|------|-------|
| `Mode` | enum `Text`, `Hex` | chosen by `Auto` or forced by the user |
| `Text` | string | rendered lines (text decoded as UTF-8, or hex dump lines) |
| `ShownBytes`, `TotalBytes` | long | for the "first X of Y" note |
| `Truncated` | bool | `ShownBytes < TotalBytes` |

Limits: text 1 MiB, hex 256 KiB; Auto decision from the first 8 KiB (R-10).

## Extraction

| Entity | Fields |
|--------|--------|
| `OperationRequest` (extended, `Kind = ExtractFiles`) | source (`ImagePath` or `Device`), `Partition`, `EntryPath`, `DestinationFolder`, `ConflictPolicy` (`Ask`, `Replace`, `Skip`) |
| `ExtractionPlan` | list of `ExtractionItem`, total files, directories, bytes, conflicts count, issues found while planning |
| `ExtractionItem` | source `FsEntry`, relative destination path (after name mapping), action `Copy`, `CreateDirectory`, `SkipSymlink`, `SkipSpecial`, `SkipExisting`, `Fail(reason)` |
| `ExtractionIssue` | `Kind` (`Skipped`, `Renamed`, `Failed`), original path, resulting name (renames), reason |
| `ExtractionSummary` (in `OperationResult`) | files and directories extracted, bytes, issues, destination folder |

State transitions follow the existing `OperationState`: Created → Preparing (walk, sizes,
conflicts) → Running (transfer) → Finalizing (directory times) → Completed / Cancelled / Failed.
A `Failed` item does not fail the operation; the operation fails only for guard violations,
destination write errors that make continuing pointless (no space left), and source device errors.

## Name mapping (`WindowsNames`)

Input: decoded name and the set of names already used in the destination directory (case-
insensitive). Output: the Windows name and a flag `Renamed`. Deterministic: entries are processed
in the sorted order of the view, so the result does not depend on read order.

## Validation rules (from the spec)

| Rule | Where | Spec |
|------|-------|------|
| source never written | read-only devices only; `ReadOnlyBlockDevice` test fake | FR-019, SC-004 |
| destination ≠ image, not on browsed disk | `ImagingEngine.Validate` (`FileIdentity`, `PathInfo`) | FR-019 |
| enough free space, FAT 4 GiB limit | `ExtractionPlanner` | FR-016, edge cases |
| no silent replace | conflict policy decided before writing | FR-015 |
| no complete-looking partial file | `.hddbackup-part-<id>` in the same directory + rename | FR-014 |
| per-item failure continues | `ExtractionIssue` list | FR-018, FR-021 |
