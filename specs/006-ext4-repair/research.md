# Research: Repair of ext4 File Systems After an Unclean Shutdown

## R-1. Repair by rebuild, reusing the shrink pipeline

- **Decision**: a repair is a rebuild of the ext4 partition into a new image, exactly like
  shrinking (feature 002), with the source read through a **journal overlay** and with orphan
  inodes left out. With **Same as the image** the rebuild targets the original partition size;
  with a shrink size, the smaller size. One code path serves repair, shrink, and repair + shrink.
- **Rationale**:
  - The rebuilt file system is created from scratch: bitmaps, free counts, group descriptors,
    checksums and the clean flag are always correct (FR-005) without a separate fixer.
  - The rebuild walks the directory tree from the root, so unlinked orphan inodes are not copied
    (FR-004) — their space is free in the result by construction.
  - The existing verification applies unchanged: full `Ext4Checker` run on the output and
    `Ext4TreeComparer` against the (overlaid) source, so every file, link, owner, mode and xattr
    of the recovered source is proven present in the result (FR-007).
  - The writer is already validated against `e2fsck -fn` on all fixtures (feature 002).
- **Consequence**: inode numbers change, as for shrinking (documented in feature 002 as by
  design); file contents and metadata are identical.
- **Alternatives considered**: repairing a byte copy in place (apply journal blocks, release
  orphans, recompute bitmaps and counts, rewrite descriptors): keeps inode numbers, but needs a
  new, untested metadata writer and gives a weaker verification (no independent tree to compare
  against). Porting e2fsck's recovery code: GPL-2 (constitution, feature 002 R-1).

## R-2. Journal replay (jbd2)

- **Decision**: `Jbd2Journal` reads the internal journal (inode 8, or `s_journal_inum`) through
  the existing reader and builds an overlay `block number → newest block content` from all
  complete transactions, following the jbd2 on-disk format documentation (kernel
  `Documentation/filesystems/ext4/journal.rst`):
  - journal superblock v1/v2 (`JBD2_SUPERBLOCK_V1/V2`), `s_start` = 0 means empty;
  - scan from `s_first`/`s_start` with expected sequence `s_sequence`; stop at the first block
    that is not a descriptor/commit/revoke with the expected sequence;
  - descriptor block tags: 32- or 64-bit block numbers (`INCOMPAT_64BIT`), tag size for
    `CSUM_V2/V3`, `SAME_UUID`, `LAST_TAG`, `ESCAPE` (restore the magic in the first 4 bytes);
  - revoke blocks: a revoked block is not replayed from transactions up to and including the
    revoking one;
  - commit block: the transaction counts only when its commit block is present; with
    `CSUM_V2/V3`, descriptor, revoke and commit block checksums (crc32c seeded with the journal
    UUID) and per-tag data checksums must match, otherwise this transaction and all later ones
    are discarded (spec edge cases);
  - `ASYNC_COMMIT`: commit block checksum covers the transaction; a mismatch discards it.
- **Supported scope**: internal journal only (external journal devices are refused as an
  unsupported feature); `INCOMPAT_FAST_COMMIT` areas are ignored only when the fast-commit area is
  empty, otherwise refused.
- **Rationale**: this is the standard recovery algorithm; implemented from the format
  documentation in managed code (no GPL code, constitution "File systems").
- **Overlay**: `OverlayBlockDevice` wraps the read-only partition view and returns overlay blocks
  instead of the on-disk ones; everything above it (superblock, descriptors, inodes, extents,
  directories) sees the recovered file system.

## R-3. Orphan inodes

- **Decision**: walk the orphan list (`s_last_orphan`, next pointer in `i_dtime`, guarded against
  loops and invalid numbers) on the overlaid file system:
  - inodes with `links_count = 0` (deleted while open) are not reachable from the tree and are
    simply not copied;
  - inodes with `links_count > 0` (truncate in progress) are copied with data only up to
    `i_size`, as e2fsck does when it finishes the truncate.
- `INCOMPAT_ORPHAN_FILE` (orphan file, e2fsprogs ≥ 1.47 defaults off) is treated as unsupported
  unless empty.

## R-4. What counts as power-loss damage

- **Decision**: classify `Ext4Checker` findings run on the overlaid source:
  - **Expected after power loss** (repaired by the rebuild): bitmap entries and checksums, free
    block/inode counts (group and superblock), group directory counts, inodes marked used but
    unreachable (orphans), "journal is not empty", missing clean flag.
  - **Structural** (refused, FR-006): blocks used twice, directory entries with invalid or free
    inodes or wrong file types, '.'/'..' errors, directories linked twice, wrong link counts,
    wrong i_blocks, directory size mismatch, resize inode errors, xattr reference counts,
    unreadable metadata (format exceptions), recorded file system errors
    (`EXT4_ERROR_FS` / `s_error_count`).
- `Ext4Checker` returns structured findings (`Ext4Finding { Kind, Message }`) instead of plain
  strings; existing callers use the messages as before.
- **Rationale**: Q2 = A. Recorded errors are refused because the kernel records them for real
  inconsistencies; the advice is to run a Linux file system check.

## R-5. Planner and shrinker changes

- **Decision**:
  - `ShrinkPlanner.Analyze` reports per ext4 partition a `FileSystemState`: `Clean`,
    `NeedsRepair` (reasons: journal, orphans, not clean) or `Damaged` (structural findings).
    Journal, orphan and not-clean conditions are no longer blockers when a repair is requested;
    the plan's sizes (used data, minimum) are computed on the overlaid source.
  - `ShrinkRequest.Repair` (bool). With `Repair` the shrinker reads the source through the
    overlay, skips orphans, and accepts a target equal to the current image size (no
    `AlreadyFits` blocker) for a same-size repair.
  - Without `Repair`, a file system that needs repair is still refused (old behavior), so the UI
    always passes the user's choice explicitly.
- **Scope**: the root (last) ext4 partition, as for shrinking. Other ext4 partitions before it are
  copied byte for byte and reported in the analysis as "not repaired" when they need repair
  (spec assumption narrowed: repair of non-last ext4 partitions is out of scope, listed in the
  analysis).

## R-6. Engine and UI

- **Decision**:
  - `OperationKind.ShrinkImage` gains `OperationRequest.RepairFileSystem`; Image tab
    "Same as the image" + repair sends `ShrinkImage` with `ShrinkTargetBytes = current size` and
    `RepairFileSystem = true` (format of the output from its extension; source format allowed).
    Without repair, "Same as the image" stays `ConvertImage` (feature 005).
  - Phase labels: a new Preparing sub-step "Replaying the journal…" when the overlay is built.
  - Image tab: "File system" section in the analysis (per partition state and reasons), check box
    **Repair the file system** (checked and enabled when the root partition needs repair; required
    for shrink sizes in that case; hidden when clean). Default name `<name>_repaired.img[.gz]`
    (`ShrinkOutputName` gains a repaired variant).
  - Restore tab: `ImageFit` treats `NeedsRepair` as shrinkable; the warning and the
    "Shrink and write" offer say the file system will be repaired; the shrink step passes
    `RepairFileSystem = true`.

## R-7. Test fixtures with real damage

- **Decision**: extend `tests/fixtures/ext4/make-fixtures.sh` (Docker, `debian:bookworm`,
  e2fsprogs 1.47):
  - **journal**: write transactions into the journal without replaying them with `debugfs -w`
    journal commands (`journal_open`, `journal_write -b <blocks> <file>`, `journal_close`) so that
    file data and metadata blocks only exist in the journal; set `needs_recovery`; the reference
    result is `e2fsck -fy` on a copy (replays the journal).
  - **journal with a torn last transaction**: the same with the last commit block zeroed.
  - **orphans**: unlink open-style inodes with `debugfs` (`unlink`, `sif links_count 0`,
    `ssv last_orphan`), plus one truncate orphan with blocks beyond `i_size`.
  - **not clean**: `ssv state 0`, wrong free counts in the superblock and a group descriptor.
  - **structural damage**: a directory entry pointing to a cleared inode (`clri`) — must be
    refused.
  - Each fixture ships a manifest of the expected tree after `e2fsck -fy`.
- **Verification**: `check-output.sh` (`e2fsck -fn` on the repaired output, tree comparison with
  the e2fsck-recovered reference) is run by the existing `ext4-check` CI job.
- **Confirmed (T001, 2026-10-17)**: e2fsprogs 1.47.0 `debugfs` has `journal_open` (`jo -c`
  enables checksums v3), `journal_write`, `journal_close`. The fixtures write the block
  differences between the original and the changed file system as journal transactions; after
  `e2fsck -fy` the recovered tree equals the changed one.
- **Change from the plan**: the references are stored as images (`<name>.reference.img.gz`,
  the fixture after `e2fsck -fy`) instead of manifests; tests and the CI check compare trees with
  them directly, so no hand-written mirror of the changes is needed. The damaged fixture is also
  marked not clean, as such cards are in practice, so the analysis reads it fully.
- `check-output.sh` installed its tools only when `e2fsck` was missing; `debian:bookworm` ships
  `e2fsck` but not `sfdisk`, so whole images were checked instead of the root partition. It now
  checks for every tool it uses.
- Synthetic journals in `Jbd2JournalTests` cover revoke, escape, damaged data, a missing commit
  and the 8/12/16-byte tag formats.

## R-8. Constitution amendment

- **Decision**: amend Principle VII (MINOR, 1.1.0 → 1.2.0): the scope includes "repairing the ext4
  file system of an image after an unclean shutdown (journal replay, orphan inodes, free space
  accounting) by rebuilding it into a new image; the original image is never modified; other
  damage is refused". Update the Technology section ("ext4 is read, repaired and written by own
  managed code"). Sync Impact Report at the top of the constitution.
