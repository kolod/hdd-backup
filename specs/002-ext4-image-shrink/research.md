# Research: Shrink ext4 Image to Fit a Smaller Card

## R-1. Approach: in-place shrink vs. rebuild ("repack")

- **Decision**: **Rebuild.** Read the source ext4 file system with an own read-only reader. Create a
  new, smaller ext4 file system in the output image with an own writer that copies the same
  features, UUID, label and hash seed. Copy every inode (content, metadata, extended attributes,
  links) into it. Partitions before the ext4 partition are copied byte-for-byte; only the last
  partition entry's length changes.
- **Rationale**:
  - **Correctness.** An in-place shrink (the resize2fs algorithm) must relocate blocks and inodes,
    renumber inodes in every directory, rewrite extent trees, move the journal, flex_bg bitmaps and
    inode tables, and keep every checksum consistent while doing so. That is a large, delicate
    state machine, and a bug destroys data. A rebuild writes only fresh, simple structures.
  - **Verifiability (FR-007).** The result can be read back with the same reader and compared
    object by object with the source: names, types, content hashes, modes, owners, timestamps,
    xattrs, link targets and hard-link groups. A rebuild never modifies the source, so the
    comparison is always possible.
  - **Licensing.** e2fsprogs (`resize2fs`) is GPL-2. Porting it would make the whole executable
    GPL. An own implementation from the published on-disk format documentation has no such effect.
  - **Bonus.** The result is defragmented: files are contiguous, which also makes it compress
    better.
- **Consequences**: inode numbers change, which nothing in a normal Linux root file system depends
  on; hard links stay grouped. Block positions change. Root mounting by `PARTUUID` (partition
  table disk ID, which is kept) or by file system `UUID` (copied) keeps working.
- **Alternatives considered**:
  - Port of `resize2fs` to C#: about 3 000 lines of tightly coupled C, GPL-2, highest risk.
  - DiscUtils (MIT) `Ext` module: read-only, no ext4 writer, several extra assemblies (against
    Principle III); its reader could have been reused, but our reader needs only a small subset.
  - lwext4 (C library): GPL-2 and native code (no single AnyCPU executable).
  - Running Linux tools (WSL, VM, Docker): explicitly excluded by the user.

## R-2. Supported ext4 feature set

- **Decision**: accept the features of current Raspberry Pi OS / Debian / Ubuntu images, reproduce
  them in the output, and refuse everything else with the feature named.

| Feature | Read | Write | Note |
|---------|------|-------|------|
| has_journal | yes | yes (new empty internal journal, same size if it fits) | journal contents not copied; requires clean FS |
| ext_attr | yes | yes (in-inode and external xattr blocks) | ACLs, SELinux, capabilities preserved |
| resize_inode | yes | yes (reserved GDT blocks + inode 7, as mke2fs does) | keeps online grow (first-boot expand) working |
| dir_index | yes (read leaf blocks linearly) | flag kept; directories written linear (valid ext4); htree optional later | kernel indexes on demand |
| filetype, extent, flex_bg, sparse_super, large_file, huge_file, dir_nlink, extra_isize | yes | yes | |
| 64bit | yes | yes | 64-byte group descriptors |
| metadata_csum (+ csum_seed) | yes (verify) | yes (crc32c everywhere) | |
| block maps (non-extent files, from ext3 history) | yes | written as extents | |
| inline_data, encrypt, verity, bigalloc, quota, project, casefold, meta_bg, external journal, journal_dev | — | — | **refuse** (FR-005) |

## R-3. Preconditions (FR-005)

- `s_state` must have VALID_FS and no ERROR_FS; `s_last_orphan` must be 0; the journal must be
  empty (needs_recovery feature not set). Otherwise refuse with "repair on the original device".
- The reader verifies the superblock, group descriptor, inode and extent checksums when
  metadata_csum is enabled; any mismatch causes a refusal.
- Block size 1024, 2048 or 4096; inode size 128 or 256.

## R-4. Target size and minimum size

- **Decision**: the new FS size is
  `max(requested, minimum)`, where
  `minimum = data blocks + directory blocks + xattr blocks + extent index blocks + metadata
  overhead(layout for that size) + journal + margin`, computed iteratively (overhead depends on
  size). Margin = max(128 MiB, 5% of used data) (FR-011, SC-005).
- Inode count: mke2fs default ratio (one inode per 16 KiB) for the new size, but never less than
  the used inodes + 10%.
- The partition end is aligned to 1 MiB; image length = partition end.

## R-5. Working copy for compressed sources

- **Decision**: a `.img.gz` source is first decompressed to `<output folder>/<name>.work.partial`.
  It is deleted after the run, on success or failure. A raw `.img` source is read in place
  (read-only). The output is always built as a raw `.img` in `<output>.partial`, because the
  writer needs random access. If the user chose `.img.gz`, a final compression pass follows.
  Free space is checked up front for working copy + output (FR-006).
- **Rationale**: ext4 metadata reading needs random access, and gzip has none. Files stay inside
  the user-chosen folder (Principle III: no files elsewhere).

## R-6. Verification (FR-007)

- **Decision**: three checks before success:
  1. Own structural checker on the output: superblock and group counts, bitmaps vs. actual use
     (rebuilt from inode extents), every checksum, directory `.`/`..`, link counts, the free
     counts in the superblock and descriptors, resize inode layout, journal superblock.
  2. Tree comparison source ↔ output via the reader. Per path: type, mode, uid, gid,
     atime/mtime/ctime/crtime incl. extra nanoseconds, size, SHA-256 of content, symlink
     target, device numbers, xattrs (name/value), hard-link group membership.
  3. Partition table and the bytes before the last partition are identical to the source.
- Independent `e2fsck -fn` (SC-001) runs only in the developer test suite (Docker, R-8).

## R-7. Writer design (summary)

- Layout like mke2fs with flex_bg (16 groups per flex group): superblock and GDT backups in groups
  0, 1 and powers of 3, 5 and 7. Reserved GDT blocks for resize_inode. Bitmaps and inode tables
  are packed at the start of each flex group.
- Allocation is sequential: directories first (near their parent), then file data in large
  contiguous runs. A file gets up to 4 extents in the inode; more extents use one index level
  (depth 1). Contiguous allocation makes depth 2 practically unnecessary; it is still supported
  for very fragmented free space.
- Special inodes: 1 (bad blocks, empty), 2 (root), 5–6 empty, 7 (resize), 8 (journal),
  11 (lost+found with pre-allocated blocks like mke2fs).
- crc32c (Castagnoli) is implemented in managed code with a table (no SSE4.2 dependency on Windows 7).

## R-8. Test fixtures and independent checks (developer machine only)

- **Decision**: `tests/fixtures/ext4/make-fixtures.sh` runs in a Docker container
  (`debian:bookworm`, e2fsprogs). It builds small two-partition images: FAT32 boot plus an ext4
  root with Raspberry Pi OS default features. The root contains hard links, symlinks, device
  nodes, sparse files, xattrs/ACLs, capabilities, long names, large directories, fragmented
  files, files near the end of the FS, and a dirty FS for the negative test. The images are
  committed gzip-compressed (a few MB each).
- `tests/fixtures/ext4/check-output.sh` runs `e2fsck -fn` and `debugfs`/`diff -r` comparisons
  on produced images (SC-001, SC-002). Both scripts are developer tooling; the product never
  calls them.
- **Real hardware**: SC-003 (boots on a Raspberry Pi) is part of the manual checklist.

## R-9. Integration with existing flows

- New operation kind `ShrinkImage` in `ImagingEngine` (standalone, FR-003). New phases
  `Analyzing`, `Rebuilding`, `Checking`, plus `Verifying` for the tree comparison.
- Restore flow: when `RestoreGuard` would throw `SizeMismatch` for a known-size raw or gzip image
  with an ext4 last partition, the UI calls `ShrinkPlanner.Analyze` and offers "Shrink and
  write" (FR-002, FR-003a). That runs `ShrinkImage` (output kept on disk) followed by the normal
  `RestoreDevice` of the result. One combined progress bar is shown: shrink ≈ 70%, write ≈ 30%,
  weighted by bytes.
