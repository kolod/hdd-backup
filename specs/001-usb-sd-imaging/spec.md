# Feature Specification: USB/SD Card Imaging Utility

**Feature Branch**: `001-usb-sd-imaging`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "Implement what the constitution defines; the GUI must show operation
progress, speed, and time; 100% progress must correspond to the moment when the medium can be
safely removed." Amended: "While the image is being saved, SHA-256 is computed in parallel and
stored next to the image."

## Clarifications

### Session 2026-10-15

- Q: What should the checksum output for `.img.gz` images contain? → A: Two separate files —
  `disk.img.gz.sha256` (checksum of the compressed file) and `disk.img.sha256` (checksum of the
  uncompressed content).
- Q: When is the image checksum checked during restore? → A: Only in the same pass as writing;
  no separate pre-check — once the user confirms the write, the target's previous content is
  considered expendable.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Back up an entire USB/SD medium to an image (Priority: P1)

A user inserts a USB flash drive or SD card, starts the portable utility, selects the medium
from a list of removable devices, chooses a destination file and a format (`.img` or
`.img.gz`), and starts the backup. The window shows a progress bar, current and average speed,
elapsed time, and estimated remaining time. When progress reaches 100%, the image is complete
and verified, the medium is no longer in use, and the user is told it can be removed.

**Why this priority**: Creating a faithful backup is the core value of the product; without it
nothing else is useful.

**Independent Test**: Back up a prepared medium (known MBR or GPT layout, boot code, several
partitions) to `.img` and `.img.gz`; compare the image byte-for-byte (after decompression) with
a reference dump of the medium.

**Acceptance Scenarios**:

1. **Given** a removable medium is inserted, **When** the user opens the utility, **Then** the
   medium appears in the device list with model, capacity, and partition summary, and no fixed
   or system disk appears.
2. **Given** a medium and a destination path are selected with format `.img`, **When** the user
   starts a full backup, **Then** the resulting file has exactly the medium's size and identical
   content from the first to the last sector (partition table, boot sectors, gaps, all
   partitions).
3. **Given** format `.img.gz` is selected, **When** the backup finishes, **Then** the file
   decompresses with standard gzip tools to content identical to scenario 2.
4. **Given** a backup is running, **When** the user looks at the window, **Then** they see
   percentage, bytes processed / total, current speed, average speed, elapsed time, and
   estimated time remaining, updated at least once per second.
5. **Given** a backup reaches 100%, **Then** the image file is fully written to disk, its
   checksum is shown with a "Copy" action, the medium has been released by the utility, and a
   message states the medium can be safely removed.
6. **Given** a backup to `disk.img`, **When** it completes, **Then** `disk.img.sha256` exists
   next to it, was computed in the same pass as the backup (no second read of the medium or the
   file), and verifies with a standard checksum tool (e.g., `sha256sum -c disk.img.sha256`).
7. **Given** a backup to `disk.img.gz`, **When** it completes, **Then** `disk.img.gz.sha256`
   (checksum of the compressed file, verifiable with a standard tool in the folder) and
   `disk.img.sha256` (checksum of the uncompressed content, equal to the checksum of the source
   medium) exist next to it.
8. **Given** a backup is cancelled or fails, **Then** no checksum file is left for the incomplete
   image.

---

### User Story 2 - Restore an image to a new medium (Priority: P1)

A user selects an existing `.img` or `.img.gz` file and a target removable medium. The utility
shows the target's details and the image's size, warns that all data on the target will be
destroyed, and requires explicit confirmation. The image is written (compressed images are
written directly, without first unpacking to disk), optionally read back and verified. Progress
reaches 100% only after all data is physically committed to the medium, verification (if
enabled) is complete, and the medium has been prepared for safe removal.

**Why this priority**: A backup is only useful if it can be restored; together with Story 1
this forms the MVP.

**Independent Test**: Restore an image produced by Story 1 (both formats) to a different medium
of equal or larger size; dump the target and compare with the image.

**Acceptance Scenarios**:

1. **Given** an image and a target medium are selected, **When** the user clicks Write,
   **Then** a confirmation shows target model, capacity, and existing partitions, and nothing is
   written until the user explicitly confirms.
2. **Given** the image is larger than the target, **When** the user tries to write, **Then** the
   operation is refused with a clear explanation.
3. **Given** the target is larger than the image, **When** the user writes, **Then** a warning
   states the remaining space will be left unallocated, and the write proceeds after
   confirmation.
4. **Given** a `.img.gz` image, **When** it is written, **Then** no uncompressed temporary copy
   is created and the target content equals the decompressed image.
5. **Given** verification is enabled, **When** writing finishes, **Then** the medium is read
   back and compared with the image; any mismatch is reported with its offset and the operation
   is marked failed.
6. **Given** the progress bar shows 100%, **When** the user physically removes the medium,
   **Then** no data is lost or corrupted (all OS and device write caches were flushed and the
   medium was released before 100% was shown).
7. **Given** a volume on the target is in use by another program, **When** the utility cannot
   take exclusive access, **Then** the operation aborts before writing and names the problem.
8. **Given** the image has checksum file(s) next to it, **When** it is restored, **Then** the
   checksum(s) of the image data are computed in the same pass as writing and compared; on match
   the result states "image checksum verified".
9. **Given** the image content differs from its stored checksum, **When** it is restored,
   **Then** the operation is marked failed with "image checksum mismatch" and the user is told
   the target medium content is not trustworthy.
10. **Given** an image without any checksum file, **When** it is restored, **Then** the restore
    proceeds and the result states that no stored checksum was available (the computed checksum
    is still shown).

---

### User Story 3 - Back up and restore a single partition (Priority: P2)

A user selects one partition of a medium and saves only that partition to `.img`/`.img.gz`.
Later the user restores that partition image into an existing partition of the same or larger
size on a target medium.

**Why this priority**: Useful for saving one data or boot partition without the rest, but
secondary to full-medium imaging.

**Independent Test**: Back up partition N of a known medium; compare the image with the exact
sector range of that partition; restore it to a matching partition on another medium and
compare.

**Acceptance Scenarios**:

1. **Given** a medium with several partitions, **When** the user selects partition mode,
   **Then** each partition is listed with number, label, file system, size, and offset.
2. **Given** a partition is selected, **When** backed up, **Then** the image contains exactly
   that partition's sectors and its descriptive metadata (type, size, original offset, table
   style) is saved alongside.
3. **Given** a partition image and a target partition smaller than the image, **When** the user
   tries to restore, **Then** the operation is refused.
4. **Given** a partition image is restored, **Then** only the target partition's sectors change;
   the partition table and other partitions remain byte-identical.

---

### User Story 4 - Use the utility in the user's language on any supported Windows (Priority: P2)

The user runs the same single executable (no installation) on Windows 7 SP1 through Windows 11.
The UI appears in the OS language if it is English, Ukrainian, German, French, Spanish, or
Japanese, otherwise in English, and can be switched at runtime.

**Why this priority**: Required by the constitution and essential for reach, but does not change
the core imaging behavior.

**Independent Test**: Launch the executable from a USB stick on clean Windows 7 SP1 and Windows
11 machines; switch through all six languages and confirm every screen and message is
translated and nothing is clipped.

**Acceptance Scenarios**:

1. **Given** a clean supported Windows machine, **When** the user runs the executable, **Then**
   it starts after an elevation prompt without any installation and leaves no files or registry
   entries outside its own folder and the chosen image paths.
2. **Given** the OS UI language is German, **When** the utility starts, **Then** all text is in
   German.
3. **Given** the user chooses Japanese in the language menu, **When** selected, **Then** the UI
   switches immediately without restart.

---

### User Story 5 - Cancel an operation safely (Priority: P3)

A user can cancel a running backup or restore at any moment.

**Why this priority**: Important for control and safety, but not part of the happy path.

**Independent Test**: Start each operation type and cancel at random points; verify the
application's state and the medium's release.

**Acceptance Scenarios**:

1. **Given** a backup is running, **When** the user cancels, **Then** the incomplete image file
   is deleted or clearly marked incomplete, and the source medium is released.
2. **Given** a restore is running, **When** the user cancels (after a confirmation that the
   target will be left in an unusable state), **Then** pending writes are flushed, the medium is
   released, and the user is told the medium is safe to remove but contains incomplete data.

---

### User Story 6 - Verify an existing image without restoring (Priority: P3)

A user selects an image and chooses "Verify image"; the utility recomputes its checksum(s) and
compares them with the checksum files, showing progress, speed, and time like other operations.

**Why this priority**: Convenience for checking archived backups; not required for the core
flow.

**Independent Test**: Run Verify on a good image and on a copy with one byte changed.

**Acceptance Scenarios**:

1. **Given** an image with checksum file(s), **When** the user runs Verify, **Then** the result
   is "matches" or "does not match" for each checksum file present.
2. **Given** an image with no checksum file, **When** the user runs Verify, **Then** the computed
   checksum(s) are shown and the user is offered to save them as new checksum file(s).

---

### Edge Cases

- Medium is unplugged during backup or restore: operation stops, the exact offset and cause are
  reported, partial output is marked incomplete.
- Destination disk runs out of space during backup: operation stops before writing a truncated
  image as if valid; user is told how much space is needed (for `.img`) or that space ran out
  (for `.img.gz`).
- Destination file system cannot hold files larger than 4 GB (FAT32) and the image would exceed
  it: user is warned before starting.
- Image file is a corrupt or truncated `.img.gz`: decompression error is reported with the
  offset reached; the restore is marked failed.
- Unreadable sectors on the source medium: backup stops and reports the failing offset (no silent
  zero-fill).
- Target medium is write-protected (SD lock switch): restore fails before writing with a clear
  message.
- Medium has no partition table, an unknown table, or a damaged table: full backup still works;
  partition mode shows no partitions and explains why.
- Media with logical sector size other than 512 bytes: handled with correct alignment.
- Very large media (≥ 256 GB) and compressed images larger than 4 GB: progress, speed, and time
  remain correct.
- Device list changes while the window is open (insert/remove): list refreshes automatically; a
  selected device that disappears is deselected.
- The user selects the same medium as both the image destination location and the restore target
  or source: operation is refused.
- Destination becomes full or read-only while writing a checksum file: the backup is reported
  failed; an image without its checksum file(s) is never presented as complete.
- Checksum files with the same names already exist: they are overwritten together with the image
  only after the user confirmed overwriting the image.
- Next to a `.img.gz` only one of the two checksum files is present: restore and verify use the
  one present; when both are present, both are checked and reported.
- Checksum files with upper-case hex, with or without the binary-mode `*` marker, or with Windows
  line endings are accepted when reading.
- The image was renamed so the name inside its checksum file differs: the checksum is still
  used (matched by sidecar file name) and a warning notes the name difference.

## Requirements *(mandatory)*

### Functional Requirements

**Device selection and safety**

- **FR-001**: System MUST list only removable USB and SD/microSD media, showing model, capacity,
  bus type, and partition summary, and MUST refresh the list when devices are inserted or
  removed.
- **FR-002**: System MUST never offer fixed disks, the system disk, or the disk holding the
  running Windows installation as a restore target.
- **FR-003**: System MUST open the source medium read-only during backup.
- **FR-004**: System MUST require explicit user confirmation, displaying the target's model,
  capacity, and current partitions, before writing anything to a medium.
- **FR-005**: System MUST obtain exclusive access to the target medium (all its volumes
  dismounted) before writing and MUST abort without writing if exclusive access is impossible.
- **FR-006**: System MUST refuse a restore when the image (or partition image) is larger than
  the target (or target partition), and MUST warn when the target is larger.

**Backup**

- **FR-007**: System MUST support full-medium backup covering every sector from the first to the
  last, including partition tables (MBR and GPT, including the GPT backup header), boot sectors,
  unpartitioned gaps, and all partitions.
- **FR-008**: System MUST support backup of a single selected partition, saving its exact sector
  range plus metadata (partition type, size, original offset, table style).
- **FR-009**: System MUST write images as uncompressed `.img` or gzip-compressed `.img.gz`
  readable by standard tools.
- **FR-010**: System MUST compute SHA-256 checksums in the same pass as the backup data
  transfer (no separate read of the medium or of the produced file) and display the content
  checksum on completion with a copy action.

**Restore**

- **FR-011**: System MUST restore `.img` and `.img.gz` full-medium images to a target medium.
- **FR-012**: System MUST restore `.img.gz` by decompressing on the fly with no uncompressed
  temporary file.
- **FR-013**: System MUST restore a partition image into a selected existing partition on the
  target without altering the partition table or other partitions.
- **FR-014**: System MUST offer read-back verification after restore (enabled by default) and
  report the first mismatch offset on failure.

**Progress, speed, and time**

- **FR-015**: The GUI MUST show, during every operation: progress percentage and bar, bytes
  processed and total, current speed, average speed, elapsed time, and estimated time remaining,
  refreshed at least once per second.
- **FR-016**: Progress MUST cover all phases of the operation (preparation, data transfer,
  flushing, verification if enabled, release of the medium) and MUST display the current phase
  name.
- **FR-017**: Progress MUST reach 100% only when the medium can be safely removed: all data has
  been physically committed (OS and device caches flushed), verification (if enabled) is
  complete, and the utility has released all handles to the medium. Until then, progress MUST
  stay below 100%.
- **FR-018**: At 100%, System MUST show an explicit "safe to remove" notice for the medium and,
  for backups, confirm the image file has been fully written to its destination.
- **FR-019**: Progress MUST never move backwards, and the time-remaining estimate MUST be
  smoothed so it does not jump erratically; when no reliable estimate exists, it MUST show
  "calculating…" instead of a wrong value.
- **FR-020**: The taskbar button MUST reflect the operation's progress (on systems that support
  it) so it is visible when the window is minimized.

**Errors and cancellation**

- **FR-021**: On any error (I/O failure, device removed, destination full, corrupt image,
  write-protected target) System MUST stop, report the cause and the byte offset reached, and
  mark partial output as incomplete.
- **FR-022**: Users MUST be able to cancel any operation; after cancellation the medium MUST be
  released and the user told whether the medium is safe to remove and whether its content is
  complete.
- **FR-023**: System MUST prevent closing the window during a write operation without
  confirmation, and on confirmed close MUST perform the same safe cancellation as FR-022.

**Portability, platform, and language**

- **FR-024**: System MUST run as a single executable without installation on Windows 7 SP1, 8.1,
  10, and 11 (32- and 64-bit), and MUST request administrator rights at start.
- **FR-025**: System MUST NOT write registry entries or files outside its own folder and the
  user-chosen image paths.
- **FR-026**: UI MUST default to English and be fully available in Ukrainian, German, French,
  Spanish, and Japanese, auto-selected from the OS language and switchable at runtime.
- **FR-027**: All user-visible texts (including errors and confirmations) MUST be localized, and
  layouts MUST not clip text in any supported language.

**Checksums**

- **FR-028**: For `.img` backups, System MUST write `<name>.img.sha256` next to the image.
- **FR-029**: For `.img.gz` backups, System MUST write two separate checksum files next to the
  image: `<name>.img.gz.sha256` with the checksum of the compressed file (labeled with the
  `.img.gz` name) and `<name>.img.sha256` with the checksum of the uncompressed content (labeled
  with the `.img` name).
- **FR-030**: Each checksum file MUST contain exactly one entry in the widely used
  "checksum + file name" text format, so standard tools can verify the labeled file directly.
- **FR-031**: Checksum files MUST be fully written and committed to storage before a backup
  reports 100%; on cancellation or failure no checksum file may remain for the incomplete image.
- **FR-032**: During restore, System MUST compute the checksum(s) of the image in the same pass
  as writing and compare them with the stored checksum file(s) when present; a mismatch MUST
  mark the restore as failed.
- **FR-033**: When no checksum file is present at restore, System MUST proceed and clearly state
  that the image could not be verified against a stored checksum.
- **FR-034**: Users MUST be able to verify an existing image against its checksum file(s)
  without restoring it, with the standard progress, speed, and time display (FR-015).
- **FR-035**: When verifying an image without checksum files, users MUST be offered to save the
  computed checksum(s) as new checksum file(s).
- **FR-036**: Partition images MUST additionally record the content checksum in their metadata.

### Key Entities

- **Removable Medium**: a physical USB/SD device; attributes: model, serial (if available),
  capacity, logical sector size, bus type, write-protection state, partition table style, list of
  partitions.
- **Partition**: a contiguous sector range on a medium; attributes: number, type/GUID, label,
  file system, offset, size.
- **Image**: a file containing either a full medium or a single partition; attributes: kind
  (full/partition), format (`.img`/`.img.gz`), data size, checksum, source description,
  completeness state.
- **Partition Image Metadata**: sidecar description of a partition image (type, original offset,
  size, table style, checksum).
- **Checksum File**: a text file next to an image with exactly one entry (SHA-256 value + file
  name it applies to); kind is "file checksum" (`<name>.img.gz.sha256`, or `<name>.img.sha256`
  for raw images) or "content checksum" (`<name>.img.sha256` next to a `.img.gz`).
- **Verification Result**: per checksum file: matches / does not match / not available, plus the
  computed value.
- **Operation**: a backup, restore, or verify job; attributes: phase, bytes processed, total
  bytes, current/average speed, elapsed time, remaining time estimate, result (success, failed
  with cause and offset, cancelled).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of full-medium backups restored to another medium of equal size produce a
  byte-identical medium (verified on MBR and GPT test media).
- **SC-002**: In 100% of test runs, physically removing the medium immediately after progress
  shows 100% results in no data loss or corruption on the medium or in the image.
- **SC-003**: Throughput for uncompressed backup and restore is at least 90% of the medium's
  sequential speed as measured by a reference raw-copy tool on the same machine.
- **SC-004**: After the first 10 seconds of transfer at a steady rate, the time-remaining
  estimate stays within ±15% of the actual remaining time.
- **SC-005**: A first-time user can complete a full backup in under 1 minute of interaction
  (excluding transfer time) without reading documentation.
- **SC-006**: The executable runs on Windows 7 SP1 and Windows 11 test machines without
  installing anything other than the required runtime, and is under 5 MB.
- **SC-007**: 100% of UI strings are translated into all five non-English languages, with no
  clipped text on any screen.
- **SC-008**: 0 test cases in which a fixed or system disk is offered as a write target.
- **SC-009**: Backup duration with checksum calculation is at most 5% longer than the same
  backup without it, for media up to USB 3 speeds.
- **SC-010**: 100% of produced checksum files for the image file verify successfully with a
  standard checksum tool directly in the image folder.
- **SC-011**: 100% of single-byte corruptions introduced into a test image are detected on
  restore or verify.

## Assumptions

- "Safe to remove" means: all data written and flushed from OS and device caches, all handles to
  the medium closed, and its volumes not remounted by the utility. Ejecting the medium from the
  system (equivalent to "Safely Remove Hardware") is performed automatically after the
  operation by default and can be turned off in settings; progress reaches 100% after
  this step when enabled.
- For a backup, 100% additionally means the destination image file and its checksum file(s)
  are fully flushed to their storage.
- For `.img.gz` restores and verification, progress percentage is based on uncompressed bytes
  against the uncompressed size when it is known (partition images) or can be estimated (whole-
  device images: the smallest size that covers the last partition and matches the gzip trailer,
  which stores the size modulo 4 GiB). It never falls behind the portion of the compressed file
  consumed, which is used alone when no size is available. (Amended 2026-10-16: the compressed
  portion alone showed 0.5% after 11.8 GiB of a mostly empty 29 GiB image.) Speed is reported in
  uncompressed bytes.
- Progress phases are weighted by estimated duration; verification, when enabled, accounts for a
  share proportional to its expected read time.
- Read-back verification after restore is enabled by default; verification after backup reads
  the produced image file back only if the user enables it (off by default).
- Checksum algorithm is SHA-256 only (MD5/SHA-1/SHA-512 and signing are out of scope). Entries
  are written with lowercase hex, the binary-mode `*` marker, and LF line endings.
- Restore never pre-reads the image to check its checksum before writing; the user's write
  confirmation means the target's previous content is expendable. A corrupt image is reported
  at the end of the transfer (FR-032).
- For `.img`, file and content checksums are identical, so only `<name>.img.sha256` is written.
  Next to a `.img.gz`, `<name>.img.sha256` refers to a file that is not present; it is meant for
  comparison with the medium or a decompressed copy, not for direct verification in the folder.
- Partition image metadata is stored in a small sidecar file next to the image.
- One operation runs at a time; parallel operations on several media are out of scope for v1.
- Bad-sector skipping/recovery, file-level backup, scheduling, other compression formats, and
  creating/resizing partitions are out of scope.
- The platform runtime mandated by the constitution may need to be present on Windows 7;
  installing it is the user's responsibility, and the utility (or Windows) reports clearly if it
  is missing.
