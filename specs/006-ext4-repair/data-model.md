# Data Model: Repair of ext4 File Systems After an Unclean Shutdown

## FileSystemState (per ext4 partition, in `ShrinkPlan`)

| Field | Type | Notes |
|-------|------|-------|
| `PartitionNumber` | int | |
| `Condition` | enum `Clean`, `NeedsRepair`, `Damaged` | |
| `RepairReasons` | set of `JournalNeedsRecovery`, `OrphanInodes(count)`, `NotClean` | for `NeedsRepair` |
| `Problems` | list of `Ext4Finding` (structural) | for `Damaged`, at most 50 |
| `IsRoot` | bool | only the root (last) partition is repaired |

## Journal overlay

| Entity | Fields |
|--------|--------|
| `Jbd2Superblock` | block size, max length, first, sequence, start, features (64bit, csum v2/v3, async commit, revoke), UUID, checksum type |
| `Jbd2Transaction` | sequence, list of (fs block, journal block, flags), revoked blocks, complete (commit seen), checksums valid |
| `JournalOverlay` | map fs block → content of the newest complete, non-revoked copy; replayed transaction count; discarded (torn/invalid) count |
| `OverlayBlockDevice` | read-only `IBlockDevice` over the partition view that serves overlay blocks |

Replay rules (R-2): transactions in sequence order; a transaction applies only with a valid commit
block; a revoke in transaction N suppresses copies of that block from transactions ≤ N; the first
invalid or incomplete transaction ends the replay.

## Orphan list

| Field | Notes |
|-------|-------|
| `Head` | `s_last_orphan` (after the overlay) |
| `Inodes` | chain via `i_dtime`; loop and range checked |
| per inode | `links_count == 0` → excluded from the rebuild; `> 0` → data copied up to `i_size` |

## Ext4Finding

| Field | Notes |
|-------|-------|
| `Kind` | `PowerLoss` (bitmaps, counts, checksums of bitmaps, unreachable used inodes, journal not empty) or `Structural` (everything else, see research R-4) |
| `Message` | English text as today (shown in diagnostics; the UI shows a localized summary) |

## Requests

| Field | Where | Notes |
|-------|-------|-------|
| `OperationRequest.RepairFileSystem` | `ShrinkImage` | rebuild through the journal overlay, skip orphans |
| `ShrinkRequest.Repair` | `ImageShrinker` | allows a target equal to the current size |
| `ShrinkTargetBytes` | `ShrinkImage` | current image size for "Same as the image" + repair |

## Rules

- No repair when `Condition = Damaged` or when the root partition has unsupported features.
- Without `RepairFileSystem`, `NeedsRepair` stays a blocker for shrinking (unchanged behavior).
- Non-root ext4 partitions that need repair are copied unchanged and reported as not repaired.
