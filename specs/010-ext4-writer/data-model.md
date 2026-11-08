# Data Model: Editing Files of an ext4 Partition

Types are in `HddBackup.Core.Editing` unless noted. Core returns codes and arguments; the App builds
texts (as in feature 009). No type holds anything but the user's own edits, names and block numbers.

## EditChange

| Field | Type | Notes |
|-------|------|-------|
| `Kind` | enum `EditContent`, `CreateFile`, `CreateDirectory`, `Delete`, `Rename` | |
| `Path` | string | absolute path in the file system (`/etc/hostname`) |
| `NewName` | string | `Rename` only: the new last component |
| `Content` | byte[] | `EditContent`, `CreateFile`: the new bytes (≤ 1 MiB) |
| `Original` | `OriginalStamp` | `EditContent`, `Delete`, `Rename`: inode number, size, mtime the user saw; a mismatch at save stops it |

Validation: names per R-4 (not empty, no `/` or NUL, not `.`/`..`, ≤ 255 UTF-8 bytes, not present in
the directory in the pending view); the root, `lost+found` and the entry being its own ancestor are
refused; a directory cannot be edited as content.

## DiffFile

| Field | Notes |
|-------|-------|
| `Magic`, `Version` | `HDDDIFF1`, 1 |
| `Identity` (`ImageIdentity`) | image length, partition number, offset, length, file system UUID, SHA-256 of the superblock and group descriptor area |
| `Changes` | ordered list of `EditChange` |
| `Path` | `<image>.hddbackup-diff` |

Rules: replaced atomically after every change; held with `FileShare.None` while edit mode is on;
`Validate(image)` returns `Matches`, `ImageChanged`, `Corrupt`, `Unsupported` (version).

## WriteSet

Ordered list of `BlockWrite { Offset, Data }` in **partition** coordinates, grouped into phases
(`Data`, `Allocation`, `Inode`, `Link`, `Free`, `Counters`) that mark the flush points of R-5.
`RecordingBlockDevice` is an `IBlockDevice` over the partition whose reads see the recorded writes and
whose `Write` only records.

## RedoLog

`<image>.hddbackup-redo`: header (magic `HDDREDO1`, the identity of the image **before** the save, the
partition offset), the write set (each record length-prefixed with a CRC-32C), and a trailer that
makes the file valid only when complete. Written as a temporary file and renamed. Presence of a valid
redo log means "save interrupted".

## Ext4EditSupport

`Check(Ext4Superblock)` → `EditSupport { Editable, Reason }`, `Reason` ∈ `None`, `NeedsRecovery`,
`HasOrphans`, `HasErrors`, `UnsupportedFeature(name)`, `Damaged`.

## PendingTree (IFileTree)

On-disk tree plus the changes: `GetChildren` merges new entries and hides deleted ones;
`Describe` reports edited size/time; `ReadRange` serves the new content; each `FsEntry` of the pending
view carries `PendingState` (`None`, `New`, `Edited`, `Deleted`, `Renamed`) for the tree glyphs.

## ImageEditSession (state machine)

| State | Meaning | Leaves by |
|-------|---------|-----------|
| `Off` | read-only tab | Edit |
| `Editing` | changes may be waiting (`Changes.Count`) | Save → `Saving`; Discard → `Editing` (empty); Edit off with changes → ask |
| `Saving` | writing (engine operation) | success → `Off` (checksum phase follows); failure/cancel → `Interrupted` |
| `Interrupted` | redo log present | Finish save → `Saving`; (nothing else writes) |
| `Refused` | not editable, with `Reason` | — |

## CardEditSession

`Off` → (confirmation) → `Editing` → each `Apply(change)` is one write set, verified and replayed;
failure → `Off` with the cause, offset and the change in progress. No persistent state.

## SaveResult

`Status` (`Saved`, `SavedChecksumStale`, `Interrupted`, `Refused`, `Failed`), `ChangesWritten`,
`ChecksumFiles` (paths updated or created), `ErrorCode`, `Offset`, `Reason`.

## Reason and message codes (resource strings `Edit_*`)

`EditRefused_{Compressed, NotWritable, WriteProtected, NotRemovable, SystemDisk, NeedsRecovery,
HasOrphans, HasErrors, UnsupportedFeature, Damaged, InUseByAnotherInstance}`; `ChangeRefused_{
BadName, NameExists, NotEmpty, IsRoot, NotText, TooLarge, MixedLineEndings, Protected}`;
`SaveRefused_{NoSpace, NoInodes, TooFragmented, ImageChanged, EntryChanged, CheckFailed}`.

## Validation rules (from the spec)

| Rule | Where | Spec |
|------|-------|------|
| Nothing written before Save / the card confirmation | image: diff file only; card: confirmation first | FR-002, FR-006, FR-026 |
| Writes only inside the partition | `PartitionView` | FR-016 |
| File system consistent after a change | allocator + checker preflight | FR-013 |
| Untouched entries unchanged | editor touches only listed structures; test compares trees | FR-014 |
| Diff survives restart | atomic file + identity | FR-007 |
| Interrupted save completable | redo log | FR-017 |
| Checksum updated or created (images only) | `SaveChecksums` | FR-018, FR-028 |
