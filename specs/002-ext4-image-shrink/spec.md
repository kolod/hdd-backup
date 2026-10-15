# Feature Specification: Shrink ext4 Image to Fit a Smaller Card

**Feature Branch**: `002-ext4-image-shrink`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "If the image is larger than the SD card we plan to write it to, we need
to reduce its size. Our case is two partitions: boot (FAT32) and rootfs (ext4), so the ext4
partition must be shrunk. This must work without Linux, without virtual machines or Docker
containers; ideally a port to C#."

## Clarifications

### Session 2026-10-15

- Q: How should shrinking fit into the write flow? → A: Both — a standalone "Shrink image" action
  and an integrated "shrink and write" offer when an oversized image is written (same engine;
  the shrunk copy is kept on disk).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Write an image to a smaller card (Priority: P1)

A user has a backup of a 16 GB card (FAT32 boot partition + ext4 root partition) whose ext4
partition is mostly empty, and wants to write it to an 8 GB card. Today the utility refuses
because the image is larger than the card. With this feature, the utility explains that the image
can be shrunk to fit, shows how much space the root file system actually uses and the size the
result will have, and after confirmation produces a shrunk copy of the image that fits the card.
The user then writes it with the normal restore flow, and the card boots and works exactly as
the original.

**Why this priority**: This is the problem the user has; without it, images of large cards cannot
be moved to smaller cards on Windows at all.

**Independent Test**: Take a 16 GB two-partition image whose root file system uses 3 GB; shrink it
for an 8 GB card; write it to an 8 GB card; the device boots; all files are identical to the
original (compared file-by-file); an independent file system checker reports no errors.

**Acceptance Scenarios**:

1. **Given** an image larger than the selected target card, **When** the user starts writing,
   **Then** instead of a plain refusal the utility shows the image size, the card size, the space
   the ext4 file system really uses, and whether shrinking can make it fit.
2. **Given** the used data fits on the card, **When** the user confirms shrinking, **Then** a new
   image file is created next to the original (the original is never modified), named so it is
   recognizable as a shrunk copy, with its own checksum files.
3. **Given** the shrunk image, **When** it is inspected, **Then** the partition table shows the
   boot partition unchanged and the root partition reduced so the whole image fits the card, and
   the ext4 file system inside fills the reduced partition exactly.
4. **Given** the shrunk image is written to the card, **When** the card is used in the target
   device, **Then** it boots and every file, directory, link, permission, owner, timestamp and
   extended attribute matches the original.
5. **Given** the used data does not fit on the card even after shrinking, **When** the user tries,
   **Then** the utility refuses before doing anything and states how much space would be needed.

---

### User Story 2 - Shrink an image without writing it (Priority: P2)

A user wants to make an existing image smaller (for archiving or for later use with several
small cards) without a card attached. They choose an image and either a target size or "as
small as possible", and receive a shrunk image file.

**Why this priority**: Same engine as Story 1, useful on its own (smaller archives, preparing
cards later), but secondary to the direct "write to a smaller card" need.

**Independent Test**: Shrink a 16 GB image to "minimum"; the result is only slightly larger than
the used data; it passes an independent file system check and contains identical files.

**Acceptance Scenarios**:

1. **Given** an image, **When** the user chooses "as small as possible", **Then** the root
   partition is reduced to the used data plus a small safety margin.
2. **Given** an image, **When** the user enters a target size, **Then** the resulting image is not
   larger than that size, or the operation is refused before starting if impossible.
3. **Given** a shrinking operation is running, **Then** progress, speed and elapsed/remaining time
   are shown as for other operations, and it can be cancelled, leaving no partial output.

---

### User Story 3 - Refuse what cannot be shrunk safely (Priority: P1)

A user selects an image the utility cannot safely shrink: a different layout (e.g. root partition
not last, GPT with several data partitions, LVM), an ext4 file system that was not cleanly
unmounted or has errors, or ext4 features the utility does not support. The utility explains
the reason and changes nothing.

**Why this priority**: A wrong shrink silently destroys data; refusing clearly is part of the
core value (constitution Principle I).

**Independent Test**: Feed images with each unsupported condition; each is refused with a
specific message; no output file is produced.

**Acceptance Scenarios**:

1. **Given** an ext4 file system marked as not cleanly unmounted or containing recorded errors,
   **When** shrinking is requested, **Then** it is refused with an explanation (the user must
   repair it on the original device first).
2. **Given** an ext4 file system that uses a feature outside the supported set, **When**
   shrinking is requested, **Then** it is refused and the feature is named.
3. **Given** a partition layout other than "one or more partitions followed by a last ext4
   partition", **When** shrinking is requested, **Then** it is refused.

---

### Edge Cases

- The source image is compressed (`.img.gz`): it cannot be modified in place, so an uncompressed
  working copy is needed; the utility checks free space for it and for the result before starting.
- The destination drive lacks space for the working copy or the result: refused before starting,
  with the required amount shown.
- The ext4 file system is already smaller than its partition (partition has free space after the
  file system): only the partition table needs adjusting; no file system change is made.
- The image already fits the card: no shrinking is offered.
- Files occupy blocks near the end of the file system (fragmented or recently written): they are
  relocated; their contents remain byte-identical.
- The ext4 journal or other metadata lies in the area being removed: it is relocated safely.
- Hard links, symbolic links, device nodes, sparse files, files with extended attributes or ACLs,
  inline data, and very large directories: all preserved exactly.
- Power loss or cancellation during shrinking: the original image is untouched; the partial
  output is removed or clearly marked incomplete.
- The image uses 4096-byte or 1024-byte ext4 blocks: both handled.
- After writing a shrunk image, the target system may grow the root partition again on first
  boot (some distributions do this automatically): this is neither required nor prevented.

## Requirements *(mandatory)*

### Functional Requirements

**Detection and offer**

- **FR-001**: When the image to write is larger than the target, System MUST determine whether the
  image consists of one or more partitions followed by a last ext4 partition and, if so, how much
  space the ext4 file system actually uses.
- **FR-002**: System MUST show the image size, target size, used space of the ext4 file system and
  the resulting image size, and MUST offer shrinking only if the result fits the target.
- **FR-003**: System MUST also offer shrinking as a standalone action on an image, to a given size
  or to the minimum possible size.
- **FR-003a**: When the user accepts the offer from FR-002, System MUST shrink and then write in
  one confirmed operation (the normal write confirmation still applies), keeping the shrunk image
  on disk; progress covers both stages.

**Safety**

- **FR-004**: System MUST NEVER modify the original image; shrinking always produces a new image
  file plus its checksum files (same format as backups).
- **FR-005**: System MUST refuse to shrink when the ext4 file system is not cleanly unmounted,
  reports errors, fails internal consistency checks, or uses features outside the supported set,
  and MUST name the reason.
- **FR-006**: System MUST check free space for the result (and for a working copy when the source
  is compressed) before starting.
- **FR-007**: System MUST verify the result before declaring success: the file system structures of
  the shrunk image MUST pass the utility's own consistency check, and the number of files and the
  total size of file data MUST equal the original's.
- **FR-008**: Cancellation or failure MUST leave no file that could be mistaken for a valid shrunk
  image.

**Shrinking**

- **FR-009**: System MUST reduce the ext4 file system to the requested size by relocating all file
  data and metadata located beyond the new end, preserving every file's content, attributes,
  permissions, ownership, timestamps, links and extended attributes.
- **FR-010**: System MUST update the partition table of the image so the last partition ends where
  the shrunk file system ends; all other partitions and all bytes before the last partition
  MUST remain byte-identical.
- **FR-011**: System MUST keep a safety margin of free space in the shrunk file system (so the
  system can still start and write logs), and MUST refuse a target size that leaves less.
- **FR-012**: Shrinking MUST show progress, speed and elapsed/remaining time and MUST be
  cancellable, like other operations.

**Platform**

- **FR-013**: All of the above MUST work on Windows 7 SP1 to Windows 11 inside the existing single
  executable, without Linux, virtual machines, containers, or external tools.
- **FR-014**: All new user-visible texts MUST be available in the six UI languages.

### Key Entities

- **Shrink Plan**: source image, layout (partitions), last ext4 partition, file system used size,
  requested target size, resulting partition end and image size, safety margin, list of
  blocking reasons (if any).
- **Shrunk Image**: new image file with checksum sidecars and a record of the original image it
  was derived from.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of shrunk test images pass an independent ext4 file system checker with no
  errors or warnings.
- **SC-002**: 100% of files in shrunk test images are byte-identical to the original, with identical
  metadata (permissions, owners, timestamps, links, extended attributes).
- **SC-003**: A Raspberry Pi OS–style image shrunk this way boots on real hardware in 100% of
  test runs.
- **SC-004**: Shrinking a 16 GB image with 3 GB used data to fit an 8 GB card completes in under
  15 minutes on a typical PC with an SSD.
- **SC-005**: The minimum shrink produces an image no more than 10% (or 256 MB, whichever is larger)
  above the space actually used.
- **SC-006**: 0 test cases in which the original image is modified or a damaged image is reported
  as successful.

## Assumptions

- Supported layout: MBR (and GPT) images where the ext4 partition is the last partition; all
  earlier partitions (e.g. FAT32 boot) are left untouched. This covers Raspberry Pi OS and
  similar single-board-computer images.
- Supported ext4 feature set: the defaults used by current Raspberry Pi OS / Debian / Ubuntu
  images (journal, extents, flexible block groups, 64-bit, metadata checksums, sparse
  superblocks, directory indexing, huge files, extra inode size). Encryption, verity, bigalloc,
  quota files, external journals and other uncommon features cause a refusal (FR-005).
- Only shrinking is in scope; growing file systems, shrinking FAT/NTFS/exFAT partitions, and
  shrinking directly on a physical card are out of scope.
- The file system must have been cleanly unmounted when the backup was made; if not, the user
  repairs it on the original device first.
- A compressed source image needs temporary disk space for an uncompressed working copy; this is
  the documented exception to "no temporary file" of feature 001, which applies only to streaming
  a compressed image directly to a device.
- Independent verification during testing may use Linux tools on a test machine; the product itself
  never needs them.
- Implementation is expected to follow the published ext4 on-disk format; the exact approach
  (a port of an existing tool or an own implementation) is decided in planning.
- Shrinking is available both as a standalone "Shrink image" action and integrated into writing:
  when an oversized image is written, the utility offers "shrink and write" in one step. Both use
  the same engine; the integrated flow keeps the shrunk copy on disk next to the original.
