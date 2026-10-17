# Feature Specification: Repair of ext4 File Systems After an Unclean Shutdown

**Feature Branch**: `006-ext4-repair`

**Created**: 2026-10-17

**Status**: Draft (layout approved 2026-10-17)

**Input**: User description: "A function to fix ext4 errors. The main purpose of the program is
copying SD cards of single-board computers, which are often hard to shut down properly, so the card
is left with damage to the file system structure."

## Clarifications

### Session 2026-10-17

- Q1: Repair directly on the card? → A: No. Images only; to fix a card, back it up, repair the
  image, write it back.
- Q2: How deep does the repair go? → A: Power-loss recovery only: journal replay, orphan inodes,
  bitmaps and counts, clean flag. Any other damage is refused with the problems listed and advice
  to use a Linux file system check.
- Q: Layout of the repair action? → A: Approved as drawn: an option of **Create image** on the
  Image tab, with a "File system" section in the analysis.

## Layout Mockup *(approved)*

Repair is an option of the existing **Create image** action on the Image tab (feature 005), so it
combines with the size choices: with **Same as the image** it produces a repaired copy (any format,
also the source format); with a shrink size the result is repaired and shrunk in one operation.

```text
│ ├────────┴─────────┴───────┴───────────────────────────────────────────────────────────┐│
│ │ Image file:                                          │ Size of the new image         ││
│ │ [ D:\Images\hmi.img.gz                 ] [ Browse… ] │ (•) Same as the image         ││
│ │ Compressed image, 29.2 GB, whole device              │ ( ) Smallest possible         ││
│ │ Checksum files: hmi.img.gz.sha256, hmi.img.sha256    │ ( ) Size of a device:         ││
│ │                                                      │     [ General UDisk 14.6 ▼]   ││
│ │ File system                                ← NEW     │ ( ) Card size:                ││
│ │ #2 rootfs (ext4): needs repair — the journal needs   │     [ 8 ] [ GiB ▼]            ││
│ │ recovery, 3 orphan inodes, not cleanly unmounted     │                               ││
│ │                                                      │ Output format                 ││
│ │ Shrink analysis                                      │ [ Uncompressed *     ▼]       ││
│ │ Partitions: #1 FAT32 256 MiB, #2 ext4 28.9 GiB       │ [ ] Fast compression *        ││
│ │ Used in the Linux (ext4) partition: 4.1 GiB          │                               ││
│ │ Smallest possible image: 4.6 GiB                     │ File system           ← NEW   ││
│ │                                                      │ [x] Repair the file system *  ││
│ ├──────────────────────────────────────────────────────┴───────────────────────────────┤│
│ │ Save the new image as:                                                               ││
│ │ [ D:\Images\hmi_repaired.img                                         ] [ Browse… ]   ││
│ │                                          [ Verify image ] [ Create image ]           ││
│ └──────────────────────────────────────────────────────────────────────────────────────┘│
```

- **File system** (left) lists each ext4 partition: "clean", "needs repair — <reasons>", or, in
  red, "cannot be repaired by this program — <problems>; repair it with a Linux file system check
  and back up again".
- **Repair the file system** (right; tooltip: "Applies the journal, frees orphan inodes, corrects
  free space counts and marks the file system clean") is shown checked and enabled when a
  partition needs repair; hidden or disabled otherwise. Shrink sizes require it while the file
  system needs repair.
- Default output name: `<name>_repaired.img[.gz]` (with a shrink size: `<name>_repaired_shrunk…`).
- Restore tab: the existing size warning and the "Shrink and write" offer mention that the file
  system will be repaired as part of the shrink.

## Context

Single-board computers (Raspberry Pi and similar, industrial HMIs) are often switched off by
pulling the power. The ext4 root file system on the card is then left in one or more of these
states, from most to least common:

1. **Journal needs recovery** — changes written to the journal were not yet applied to the file
   system.
2. **Orphan inodes** — files that were deleted while still open, whose space was never freed.
3. **Not cleanly unmounted** — the "clean" flag was never set; counts of free blocks and inodes
   may be out of date.
4. **Recorded errors / real structural damage** — inconsistent bitmaps, directory entries pointing
   to invalid inodes, blocks claimed twice, and similar.

Today the program detects states 1–4 and refuses to shrink such an image, advising the user to
boot the device and shut it down properly — which is exactly what these users often cannot do.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Repair an image taken from a card that lost power (Priority: P1)

A user has backed up a card from a device that was switched off by pulling the power. On the Image
tab the analysis says the file system needs repair (journal needs recovery, orphans, not clean).
The user keeps **Repair the file system** checked and presses **Create image**; the program produces a repaired copy of the image whose
ext4 file system is consistent and marked clean. The original image is not changed. The repaired
image can be written to a card, shrunk, or kept as the new backup.

**Why this priority**: this is the situation the program is used in most often; without it the
user needs a Linux machine to run a file system check.

**Independent Test**: take test images in each damaged state (built with real Linux tools, the
journal left unreplayed, orphans present, the clean flag cleared), repair them, and confirm with an
independent Linux file system check that the result is clean and that every file whose last
complete write was in the journal has the expected content.

**Acceptance Scenarios**:

1. **Given** an image whose ext4 journal needs recovery, **When** the user creates an image with
   **Repair the file system** checked, **Then** the
   repaired image's file system is clean, the journal is empty, and the changes recorded in the
   journal are present in the files.
2. **Given** an image with orphan inodes, **When** it is repaired, **Then** the orphans' space is
   freed and they no longer exist.
3. **Given** an image that was not cleanly unmounted, **When** it is repaired, **Then** the free
   block and inode counts and bitmaps are correct and the file system is marked clean.
4. **Given** a repaired image, **When** an independent Linux file system check runs on it, **Then**
   it reports no problems.
5. **Given** any repair, **When** it finishes, **Then** the original image and its checksum files
   are unchanged, and the repaired image gets its own checksum files.
6. **Given** a repair is running, **When** the user cancels it, **Then** no repaired image and no
   temporary files remain.

---

### User Story 2 - Clear explanation when damage goes beyond what can be repaired (Priority: P1)

A user's image has damage beyond power-loss recovery (anything other than an unreplayed journal,
orphan inodes and stale counts/bitmaps). The program says so before any
file is written, lists what it found in plain words, and explains the alternative (repair with a
Linux file system check, then back up again). Nothing is changed.

**Why this priority**: a repair tool must never pretend to have fixed what it has not, and must
never make things worse (constitution principle I).

**Independent Test**: images with damage outside the supported scope are refused with the specific
reason and no output file.

**Acceptance Scenarios**:

1. **Given** an image with damage outside the supported repair scope, **When** the user asks for a
   repair, **Then** it is refused before writing, with the list of problems found.
2. **Given** a repair whose result fails the final consistency check, **When** this is detected,
   **Then** no repaired image is kept and the user is told the repair failed.

---

### User Story 3 - Repair as part of shrinking and of writing to a smaller card (Priority: P2)

A user wants to shrink an image (or write it to a smaller card with "Shrink and write") but the
file system needs repair. Instead of a refusal, the program offers to repair it first; the repair
and the shrink then run as one operation with one result file.

**Why this priority**: shrinking images from unclean cards is the second most common flow; today it
is blocked by exactly these states.

**Independent Test**: shrink an image whose journal needs recovery; the result is shrunk, clean,
and passes the independent check; the original is unchanged.

**Acceptance Scenarios**:

1. **Given** a shrink of an image that needs repair, **When** the user accepts the offer, **Then**
   one result image is produced that is both repaired and shrunk.
2. **Given** the Restore tab warns that an image is larger than the card and needs repair, **When**
   the user chooses "Shrink and write", **Then** the repair is part of the shrink step.

### Edge Cases

- **Journal with a broken or truncated transaction**: only complete transactions are applied;
  the last incomplete one is discarded, as Linux does.
- **Journal checksums do not match** (journal v3 with checksums): the damaged transaction and
  everything after it are discarded.
- **Several damaged states at once** (journal + orphans + not clean): all are handled in one
  repair.
- **File system already clean**: Repair is not offered; the analysis says the file system is
  clean.
- **Unsupported ext4 features** (as listed for shrinking): refused with the feature name.
- **Partition images** of an ext4 partition: repairable like the ext4 partition of a whole-device
  image.
- **Images with several ext4 partitions**: the root (last) ext4 partition is repaired, as for
  shrinking; other ext4 partitions are copied unchanged and the analysis says they were not
  repaired (decided in planning, research R-5).
- **Compressed source**: a temporary uncompressed copy may be needed, as for shrinking; free space
  is checked first.
- **Repairing directly on the card** (no image): not supported (Q1); the user backs the card up,
  repairs the image and writes it back.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The Image tab analysis MUST report the state of each ext4 partition: clean, needs
  repair (with the reasons: journal needs recovery, orphan inodes, not cleanly unmounted), or
  damaged beyond the supported repair (with the problems found).
- **FR-002**: For a file system that needs repair, the Image tab MUST offer the option **Repair the
  file system** for **Create image**, checked by default; with **Same as the image** it produces a
  repaired copy in a user-chosen file and format (the source format is allowed), default name with
  a `_repaired` suffix.
- **FR-003**: The repair MUST apply all complete transactions recorded in the journal and leave the
  journal empty.
- **FR-004**: The repair MUST free the space of orphan inodes and remove them.
- **FR-005**: The repair MUST recompute block and inode bitmaps, free counts and group descriptor
  checksums, and mark the file system clean.
- **FR-006**: No structural repairs beyond FR-003–FR-005 are made. Any other damage found (for
  example directory entries pointing to invalid inodes, blocks claimed twice, inconsistent extent
  trees) MUST be refused before writing, with the problems listed and advice to repair the card
  with a Linux file system check.
- **FR-007**: Before a repaired image is kept, its file system MUST pass the program's full
  consistency check; otherwise nothing is kept and the failure is reported.
- **FR-008**: Partitions other than the repaired ext4 partition(s), the partition table and all
  bytes outside the repaired partition(s) MUST be copied unchanged.
- **FR-009**: The original image and its sidecar files MUST never be modified; the repaired image
  MUST get its own checksum files and, for partition images, partition information file.
- **FR-010**: Repair MUST run as an operation with progress, cancellation and a result message, and
  MUST leave no files behind when cancelled or failed.
- **FR-011**: Shrinking (Image tab and "Shrink and write") MUST offer to repair a file system that
  needs repair instead of refusing, and then run repair and shrink as one operation producing one
  result image.
- **FR-012**: Repair MUST work only on image files; it MUST never write to a device.
- **FR-013**: All new texts MUST exist in the six UI languages.

### Key Entities

- **File system state**: per ext4 partition — clean, needs repair (reasons), or damaged beyond
  repair (problems).
- **Journal transaction**: a complete set of changes recorded before the power was lost; applied
  only when complete and (if checksummed) intact.
- **Repaired image**: a new image file whose ext4 partition(s) are consistent and clean, with its
  own checksum files.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of the test images in each supported damaged state are reported clean by an
  independent Linux file system check after repair.
- **SC-002**: After repair, the content of every file in the test images matches the content the
  same files have after Linux itself recovers the same image (journal replay by the Linux kernel or
  file system check), in 100% of the cases.
- **SC-003**: No test image with damage outside the supported scope produces a repaired image; all
  are refused with a reason.
- **SC-004**: Repairing a 16 GB image of a typical single-board-computer card takes no longer than
  shrinking the same image.
- **SC-005**: A user can go from "file system needs repair" to a repaired image in at most three
  actions on the Image tab.
- **SC-006**: Cancelled or failed repairs leave no files behind in 100% of the tests.

## Assumptions

- Repair works on images, consistent with the program's rule that the original image is never
  modified; writing the repaired image to a card uses the existing Restore tab.
- The supported ext4 feature set is the one already supported for shrinking (feature 002),
  including journal checksums (metadata_csum).
- Repair is added to the Image tab; the exact layout is drawn and approved before planning, as for
  features 004 and 005.
- Extending the program's scope to repairing ext4 requires a constitution amendment (principle
  VII currently limits ext4 work to shrinking); it is proposed in the plan.
- Test images for each damaged state are produced with real Linux tools in the developer-only
  Docker setup used for feature 002; the product does not depend on Linux tools.
