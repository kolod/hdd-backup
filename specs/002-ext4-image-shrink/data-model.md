# Data Model: Shrink ext4 Image

## ShrinkPlan (result of analysis, immutable)

| Field | Type | Notes |
|-------|------|-------|
| SourcePath | string | `.img` or `.img.gz` |
| SourceIsGzip | bool | needs a working copy (R-5) |
| TableStyle | Mbr / Gpt | |
| Partitions | list of PartitionInfo | from feature 001 parser |
| RootPartition | PartitionInfo | must be the last partition, file system ext4 |
| BlockSize | int | 1024 / 2048 / 4096 |
| UsedDataBytes | long | blocks in use by files, dirs, xattrs, extent indexes |
| UsedInodes | long | |
| MinimumFsBytes | long | R-4, incl. metadata, journal, margin |
| MinimumImageBytes | long | root partition start + MinimumFsBytes, aligned to 1 MiB |
| SourceImageBytes | long | |
| BlockingReasons | list of ShrinkBlocker | empty ⇒ shrinkable |

**ShrinkBlocker** (enum, each has a localized text): `NotEnoughSpaceEvenAtMinimum`,
`RootNotLastPartition`, `RootNotExt4`, `UnsupportedLayout` (e.g. GPT with extra partitions after
root, LVM), `FsNotClean`, `FsHasErrors`, `OrphanInodes`, `JournalNeedsRecovery`,
`UnsupportedFeature(name)`, `ChecksumMismatch(where)`, `UnsupportedBlockSize`,
`UnsupportedInodeSize`.

## ShrinkRequest

| Field | Type | Notes |
|-------|------|-------|
| Plan | ShrinkPlan | |
| TargetImageBytes | long? | null ⇒ minimum; must be ≥ MinimumImageBytes |
| OutputPath | string | default `<name>_shrunk_<size>.img`; `.img.gz` ⇒ final compression pass |
| WorkingFolder | string | folder of OutputPath |

## Ext4 model (reader/writer, internal)

- **Superblock**: block/inode counts, block size, inodes per group, blocks per group, features
  (compat/incompat/ro_compat), UUID, volume name, last mounted, hash seed, default hash
  version, checksum seed, first inode, inode size, reserved GDT blocks, flex group size,
  mount options, error behavior, timestamps (copied where meaningful).
- **GroupDescriptor** (32 or 64 bytes): bitmap and inode-table locations, free counts, flags,
  checksums, itable_unused.
- **Inode**: number, mode, uid/gid (32-bit), size, times + extra (ns, crtime), links, flags,
  generation, extent root or block map, xattr block, in-inode xattrs, checksum.
- **ExtentNode**: header (depth, entries, max), leaf/index entries, tail checksum.
- **DirEntry**: inode, rec_len, name_len, file_type, name; csum tail entry.
- **XattrEntry**: name index + suffix, value (in-inode or external block, refcounted blocks are
  de-duplicated by content).

## ShrinkResult

| Field | Type |
|-------|------|
| OperationResult fields (feature 001) | state, error, duration, file SHA-256 / content SHA-256 |
| OutputPath | string |
| OutputImageBytes | long |
| CheckerFindings | list of string (empty on success) |
| ComparedObjects | long (files, dirs, links compared) |

## State transitions (ShrinkImage operation)

```text
Created → Preparing(analyze) → Running:
   [WorkingCopy (gzip only)] → CopyPrefix → Rebuild (copy inodes/data) → UpdatePartitionTable
 → Finalizing: Checking (structural) → Verifying (tree compare) → [Compress (gz output)]
 → Flushing/commit (.partial → final, sidecars) → Completed
Any error/cancel → Failed/Cancelled; all *.partial and *.work.partial deleted; source untouched.
```

"Shrink and write" = ShrinkImage (Completed) → RestoreDevice(output) with the normal guards.
