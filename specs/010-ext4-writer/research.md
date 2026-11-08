# Research: Editing Files of an ext4 Partition

All unknowns of the Technical Context are resolved below. Code references are to the repository as
of 2026-11-08 (after feature 009). ext4 semantics are those of Linux 5.x/6.x and e2fsprogs 1.47, the
independent oracle.

## R-1: Modify in place; do not rebuild

**Decision**: an in-place editor (`Ext4Editor`) that changes only the structures a change touches:
bitmaps, group descriptors, inodes, directory blocks, extent blocks, data blocks, superblock counters.

**Rationale**: a card cannot be rebuilt into a "new image" (features 002 and 006 do that), and an
image of 32 GiB should not be copied to change a line. The reader (`Ext4Reader`), the checksum code
(`Crc32c`, `Ext4Csum`) and the layout knowledge of `Ext4Writer` are reused; the shrink writer itself is
not (it lays out a whole file system from a copy plan).

**Alternatives**: rebuilding through `Ext4Writer` — wrong for cards, slow for images, changes block
numbers of untouched files (violates SC-002/FR-014); mounting through WSL or a driver — external
dependency, Principle III.

## R-2: Which file systems can be edited

**Decision**: `Ext4EditSupport.Check(superblock)` returns reason codes. Editable: ext4 with
`filetype` and `extents`, optional `64bit`, `flex_bg`, `sparse_super`, `large_file`, `huge_file`,
`dir_index`, `dir_nlink`, `extra_isize`, `metadata_csum` (or `uninit_bg` group checksums),
`has_journal` **only when the journal is clean**, 1 KiB to 64 KiB blocks. Refused with a reason:
`needs_recovery`, orphan inodes, state "has errors", `inline_data`, `encrypt`, `casefold`, `meta_bg`,
`mmp`, `ea_inode`, `largedir`, `dirdata`, `compression`, `journal_dev`, `bigalloc`, `quota`,
`project`, `verity`, `read-only` feature, and anything `Ext4Features.Unsupported` already refuses.

**Rationale**: everything Raspberry Pi OS and Debian create by default is in the editable set; the
refused features need allocation or directory structures the editor does not implement, and refusing
is safe. A clean journal can stay as it is: the editor writes metadata directly and never touches it.

## R-3: Allocation

**Decision**: `Ext4Allocator` keeps block and inode bitmaps per touched group (read on demand,
written back as part of the write set), with:

- **goal**: the group of the parent directory (inodes) or of the file's first block (blocks), then
  first fit scanning outward; whole-extent preference so a ≤ 1 MiB file normally gets 1–2 extents;
- **uninitialised groups** (`BLOCK_UNINIT`, `INODE_UNINIT`, `ITABLE_UNINIT`): `BLOCK_UNINIT` groups
  get their bitmap built in memory as `ext4_init_block_bitmap` does (superblock and descriptor backups,
  reserved descriptor blocks, and the bitmaps and inode tables of any group that live in this group
  under `flex_bg`), then the flag is cleared; `INODE_UNINIT` groups start from an all-free bitmap and
  the flag is cleared; `itable_unused` is lowered to the highest inode used;
- **accounting**: group free blocks/inodes, used directories, flags, bitmap checksums, group
  descriptor checksums (crc16 for `uninit_bg`, crc32c for `metadata_csum`) and the superblock free
  counters (64-bit aware) are updated in the same write set; the backup superblocks and descriptors are
  left as they are, as the kernel does.

**Rationale**: these are exactly the fields `e2fsck -fn` checks after an allocation; leaving an
uninitialised group alone would make a nearly full small image impossible to edit.

## R-4: Directories

**Decision**: `Ext4DirEditor` adds, removes and renames entries in linear directory blocks
(`rec_len` slack reuse, merging a removed entry into its predecessor, a new block appended when no
slack fits, directory entry tails with checksum when `metadata_csum`). A directory with the
`INDEX` flag (htree) that is modified is **rewritten linearly**: its entries are read, the blocks are
rewritten as plain blocks (trailing blocks stay as empty directory blocks), the flag is cleared and
the inode written. Names are UTF-8, at most 255 bytes, never `.`, `..`, empty, containing `/` or NUL.

**Rationale**: maintaining htrees (hash order, node splits) is a large feature of its own; a linear
directory is valid, `e2fsck -fn` accepts it (it may only suggest `-D`), and the kernel re-indexes on
the next change. Only directories the user changes lose their index.

**Alternatives**: updating the dx nodes (hash of every name, leaf splits) — far more code and risk for
a speed benefit nobody asked for.

## R-5: Order of writes (what an interruption leaves)

**Decision**: the editor produces, for every change, an **ordered** write set in these phases, and
every replay (image save, card change) writes it in that order with a device flush between phases:

| Change | Order | If interrupted |
|--------|-------|----------------|
| Replace content | new data blocks → bitmaps/descriptors/superblock for them → **one inode write** (new extents, size, times) → bitmaps/counters that free the old blocks | content is old or new, never mixed; leaks the new or the old blocks |
| Create file/dir | data blocks → inode and bitmaps/counters → **directory entry** | before the entry: an unreferenced inode and blocks; after: complete |
| Delete | **directory entry removed** → inode (links 0, deletion time) → bitmaps/counters (children bottom-up) | after the unlink: unreferenced blocks and inode; nothing points to freed data |
| Rename | one directory block write (one or two blocks) | before: old name; after: new name |

**Rationale**: with no journal, the safe rule is "make data and allocation durable before anything
refers to it, and remove references before freeing". The worst result is a leak (blocks or an inode
that are allocated but unreferenced), which a file system check on Linux removes (`e2fsck` reconnects
or frees them) and which never loses or corrupts a file. A directory block write is not atomic at
sector level; that residual risk exists for every writer without a journal and is stated in the docs.

**Alternatives**: writing through the ext4 journal (a jbd2 writer) — the "proper" way, but it leaves
`needs_recovery` set, which Windows cannot replay, which feature 008 shows as "not cleanly unmounted"
and which fails the clean-result criteria (SC-001); a large new component for a benefit only Linux
would collect. Rejected.

## R-6: Content, extents, inodes

**Decision**: content is replaced **copy-on-write** (allocate new extents, write data, then switch the
inode, then free the old blocks); a file of ≤ 1 MiB needs at most the four extents of the inode, and a
depth-1 leaf block is used when free space is fragmented (more than the leaf capacity is refused with
"too fragmented"). New inodes get `mode` 0644/0755, the parent's uid/gid, current times (including
`crtime` when `extra_isize` allows), `i_generation` random, extents flag, `links_count`, inode
checksum; a new directory gets a block with `.` and `..` (and tail) and raises the parent's link
count. Deleting frees extent blocks (including index blocks via `ExtentTreeBlocks`), slow symlink
blocks, the xattr block (reference count decremented, freed at zero), honours hard links
(`links_count`), and recurses bottom-up through directories. Timestamps: edited file and touched
directories get mtime/ctime = now; superblock `s_wtime` = now.

**Rationale**: copy-on-write gives the old-or-new guarantee of R-5 for free and makes growing and
shrinking one code path.

## R-7: Preflight verification before the first real write

**Decision**: the ordered write set is first applied to a `RecordingBlockDevice` that stacks over the
partition (reads see the recorded writes, nothing goes to the target), and the file system **as it would
be** is checked with the product's own `Ext4Checker`; any finding stops the operation before the first
real write ("internal check failed", never written). The time of the full check is measured on the
16 GiB-class fixture (task); if it exceeds 20 s the check is limited to the structures the change touched
plus recomputed counters of the touched groups, and the full check stays in the tests and in CI.

**Measured (feature 010, Windows 11, image on a USB/HDD volume)**: full check over the overlay on a 15 GiB image (14.7 GiB ext4): 12.3 s cold; a whole Save of one new file on a copy of that image: prepare + check 1.4 s (warm), write 0.5 s, checksum 9.2 s (warm) = 11.2 s; on a 321 MiB image the whole save takes 0.4 s. The full check stays (below 20 s); the targeted check was not needed. A partition of about 24 GiB or more would exceed the threshold; then the targeted check must be written.

**Rationale**: an editor bug must not reach a card. The checker already exists and is trusted by the
repair feature; reusing it costs no new code.

## R-8: Image save, redo log and finishing an interrupted save

**Decision**: `Save` on an image = (1) open the image read/write exclusively (`FileShare.None`);
(2) verify the diff's identity and that the file system is still editable; (3) run the editor and the
preflight to get the write set; (4) write the write set to `<image>.hddbackup-redo` (header with the
image identity and a CRC-protected list of `(offset, length, data)`, created as a temporary file,
flushed, renamed) — from this moment the save is committed; (5) replay the write set in order, flush;
(6) delete the redo file, then the diff file; (7) recalculate the checksum files. If the program dies
after (4), the next open finds the redo file and offers **Finish save**, which replays the same
writes (absolute offsets, so repeating is harmless) and continues at (6). If it dies before (4), the
diff file is intact and the image has not been written.

**Rationale**: a redo log makes the save idempotent without a journal inside the file system, gives
exactly "finishing gives the result of an uninterrupted save" (SC-004), and costs only the size of the
write set (metadata plus the new file data).

**Alternatives**: copy-then-swap of the whole image — doubles the disk space and time for 32 GiB;
undo log — would need the old contents and gives rollback instead of completion.

## R-9: The diff file

**Decision**: `<image>.hddbackup-diff`, a small binary container: magic and version; the **identity**
of the image (length, partition number, offset, length, file system UUID, SHA-256 of the superblock
and the group descriptor area); then the ordered `EditChange` records (kind, path, new name, the
expected original inode number/size/mtime for edits and deletes, the new content for edits and
creations). It is rewritten atomically (temporary file, flush, replace) after every change and held
open with `FileShare.None` while edit mode is on, which also keeps a second instance out.

**Rationale**: the identity detects "the image changed since" (FR-010) without hashing the image;
the expected-original fields detect that the very file being edited changed; atomic replacement means
the diff is never half-written. Size limits: an edited file ≤ 1 MiB, ≤ 10 000 changes, ≤ 256 MiB in
all.

## R-10: Cards

**Decision**: `IDeviceAccess.OpenForPartitionWrite(device, partitionOffset, partitionLength)` opens
the physical disk writable **without locking or dismounting any volume**, checks
`IOCTL_DISK_IS_WRITABLE`, and returns a `PartitionView` over the range, so a write outside the
partition throws before reaching the disk. Preconditions at "Edit" time: removable bus, not
`SystemDiskGuard`-protected, not `HostsExecutable`, writable, partition ext4 and editable (R-2). Each
change builds its write set, verifies it (R-7) and replays it (R-5) immediately; there is no redo log
and no diff file (FR-027, SC-009). The tab reloads the changed directories after every change.

**Rationale**: Windows refuses direct writes only to sectors of **mounted** volumes; the ext4
partition is not one, so the FAT boot volume stays mounted and untouched. This is a Windows behaviour
that must be confirmed on real hardware and on Windows 7 (manual checklist); if a system refused such
writes the edit mode for cards would say so and stay off.

**Alternatives**: `OpenExclusiveForWrite` — dismounts the boot volume for no reason.

## R-11: Checksums

**Decision**: after a successful image save, `SaveChecksums` recomputes SHA-256 of the whole image (a
streaming read with progress and cancel) and rewrites every checksum file that exists beside the
image for the **uncompressed** image (`<image>.sha256`, same format via `ChecksumFile.Write`); if none
exists it creates `<image>.sha256`. Files that describe a compressed twin (`.img.gz.sha256`) are not
touched. On error or cancel the result says "the image was changed; its checksum is stale" and offers
the Image tab's **Calculate checksums**. No checksum for cards.

**Rationale**: the hash covers every byte, so it cannot be updated incrementally; the time is the time
of **Verify image**, which is why SC-007 limits only the write itself.

## R-12: Text editing in the viewer

**Decision**: in edit mode the viewer `TextBox` becomes writable for a regular file that decodes as
UTF-8 (no invalid bytes, no NUL) up to 1 MiB with consistent line endings (all LF or all CRLF);
the text is shown with CRLF for the control and stored back with the file's own line ending and its
original "ends with a line break" state; a file with mixed endings, binary data or invalid UTF-8 is
shown read-only with the reason. The BOM, if any, is kept. New files are LF.

**Rationale**: "keep exactly what is in the file" (spec edge case) without a hex editor.

## R-13: Engine integration and UI states

**Decision**: an image **Save** is an engine operation (`OperationKind.SaveEdits`, phases Prepare,
Write, Checksum) so it reuses the progress panel, cancel and result banner of features 001–008; card
changes and diff-file updates are quick direct calls on a worker thread with a "Writing…" status.
After a save the tab closes and reopens its source so tree and viewer show what is on disk.
`PendingTree` is used for the tree, viewer and Extract preview only while changes are waiting;
Extraction and the Tools of feature 009 use the on-disk view and say so (FR-023).

## R-14: Tests and independent oracles

**Decision**:

- **Fixtures** (`make-editing-fixtures.sh`, Docker): `edit-basic` (4 KiB blocks, `metadata_csum`,
  `flex_bg`, an htree directory of 600 files, hard links, an xattr block, a symlink, an empty and a
  large file), `edit-1k` (1 KiB blocks), `edit-nocsum` (`uninit_bg` only), `edit-full` (a few blocks
  free), `edit-uninit` (many uninitialised groups), plus the existing `rpi-like-4k` (MBR with FAT and
  ext4) for the card tests.
- **Own checks**: `Ext4Checker` on every result, tree and content comparison through the reader.
- **Independent**: the tests export every saved image (names `saved-<scenario>.img`) and the expected
  tree listing; the `ext4-check` job runs `e2fsck -fn` on each and compares `debugfs rdump` with the
  listing (`check-editing.sh`).
- **Interruption**: a `FaultyBlockDevice` that fails at write N, for every N of five scenarios; the
  save finished from the redo log must equal the uninterrupted image byte for byte; a card change
  interrupted at write N must leave a file system that passes the checker apart from leaks and never
  has an entry pointing at unwritten data.
- **Safety proofs**: a recording device proves zero writes outside the partition and zero writes in
  browse mode; the image SHA-256 is unchanged until Save.

## R-15: Limits and non-goals

Edited files ≤ 1 MiB; ≤ 10 000 changes and 256 MiB of diff per session; no resizing, no move between
directories (rename only changes the name), no change of permissions, owner or times by the user, no
extended attributes, no special files, no undo beyond the editor text box, no editing of `lost+found`
or the root directory entry itself, no journal use. Deleting is of any size.
