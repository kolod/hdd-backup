# Feature Specification: Image Conversion and Checksum Calculation

**Feature Branch**: `005-image-conversion`

**Created**: 2026-10-16

**Status**: Draft (layout approved 2026-10-16)

**Input**: User description: "New functions for working with an image: convert a compressed image
to an uncompressed one; convert an uncompressed image to a compressed one; calculate and save the
checksum (if the checksum files are missing)."

## Clarifications

### Session 2026-10-16

- Q: Placement and layout of the new functions? → A: Approved as drawn: on the Image tab,
  conversion via the "Same as the image" size choice, checksum calculation via the verify button.

## Layout Mockup *(approved)*

The three functions live on the existing **Image** tab (feature 004), which already holds every
action that works on an image file without touching a device. Conversion is the existing "create
a new image" action with a new size choice, **Same as the image**; checksum calculation is the
existing verify button, which changes its label when the image has no checksum files.

```text
│ ├────────┴─────────┴───────┴───────────────────────────────────────────────────────────┐│
│ │ Image file:                                          │ Size of the new image         ││
│ │ [ D:\Images\raspios.img.gz             ] [ Browse… ] │ (•) Same as the image         ││
│ │ Compressed image, 3.9 GB, whole device               │ ( ) Smallest possible         ││
│ │ No checksum files found next to the image.           │ ( ) Size of a device:         ││
│ │                                                      │     [ Generic SD 14.8 GB ▼]   ││
│ │ Shrink analysis                                      │ ( ) Card size:                ││
│ │ Partitions: boot FAT32 512 MB, rootfs ext4 14.3 GB   │     [ 8 ] [ GiB ▼]            ││
│ │ Used in the Linux (ext4) partition: 3.1 GB           │                               ││
│ │ Smallest possible image: 3.6 GB                      │ Output format                 ││
│ │                                                      │ [ Uncompressed *     ▼]       ││
│ │                                                      │ [ ] Fast compression *  (off) ││
│ ├──────────────────────────────────────────────────────┴───────────────────────────────┤│
│ │ Save the new image as:                                                               ││
│ │ [ D:\Images\raspios.img                                              ] [ Browse… ]   ││
│ │                                   [ Calculate checksums ] [ Create image ]           ││
│ └──────────────────────────────────────────────────────────────────────────────────────┘│
```

- **Same as the image** + a format different from the source = conversion (`.img.gz` ↔ `.img`).
  Available for every image, including partition images and images that cannot be shrunk.
- The other sizes work as today (shrinking a whole-device image with an ext4 last partition).
- **Calculate checksums** is shown instead of **Verify image** when the image has no checksum
  files; when it has them, the button is **Verify image** as today.
- **Create smaller image** is renamed **Create image**, since the result is not always smaller.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Convert a compressed image to an uncompressed one (Priority: P1)

A user has `disk.img.gz` and needs a plain `disk.img`, for example for a tool that cannot read
gzip or to mount it elsewhere. On the Image tab they select the image, keep **Same as the image**,
choose **Uncompressed**, accept the suggested name `disk.img` and press **Create image**. The
uncompressed image is written next to the original with its checksum file; the original is not
changed.

**Why this priority**: the most common conversion; uncompressed images are what most other tools
expect.

**Independent Test**: convert a compressed fixture image and compare the result byte for byte with
the known uncompressed content and its checksum.

**Acceptance Scenarios**:

1. **Given** a compressed image, **When** the user converts it to uncompressed, **Then** the result
   is identical to the decompressed content and a checksum file for it is saved next to it.
2. **Given** the compressed image has a checksum of its content, **When** it is converted, **Then**
   the content read during conversion is checked against that checksum, and a mismatch stops the
   conversion with an error and leaves no result file.
3. **Given** a partition image with its partition information file, **When** it is converted,
   **Then** the result also gets a matching partition information file, so it can still be
   written into a partition.
4. **Given** a conversion is running, **When** the user cancels it, **Then** no result file and no
   temporary file remain.

---

### User Story 2 - Convert an uncompressed image to a compressed one (Priority: P1)

A user has a large `disk.img` and wants to store or send it compressed. They select it, keep
**Same as the image**, choose **Compressed** and press **Create image**. The result
`disk.img.gz` is saved with both checksum files (of the compressed file and of its content).

**Why this priority**: saves disk space for archived images; symmetric to story 1.

**Independent Test**: compress an uncompressed fixture and check that standard tools decompress it
to the original bytes and that both checksum files match.

**Acceptance Scenarios**:

1. **Given** an uncompressed image, **When** the user converts it to compressed, **Then** the
   result decompresses with standard tools to exactly the original bytes.
2. **Given** the conversion finished, **When** the user looks at the folder, **Then** the
   compressed image has a checksum file for the compressed file and one for its content, like a
   compressed backup.
3. **Given** the fast compression choice exists for backups, **When** the user converts to
   compressed, **Then** the same choice (fast, larger file) is available.

---

### User Story 3 - Calculate and save checksums for an image without them (Priority: P2)

A user has an image without checksum files (copied from elsewhere or made by another tool). On the
Image tab the description says no checksum files were found, and the button reads **Calculate
checksums**. Pressing it reads the image once, with progress, and saves the checksum files next to
it, without asking a question first. Afterwards the description lists the new checksum files and
the button reads **Verify image**.

**Why this priority**: the program already offers to save checksums after verifying such an image,
but the user has to know to press "Verify" first; a direct action is clearer.

**Independent Test**: remove the checksum files of a fixture, calculate them, and compare with
checksums computed by a standard tool.

**Acceptance Scenarios**:

1. **Given** an uncompressed image without checksum files, **When** the user presses **Calculate
   checksums**, **Then** a checksum file for the image is saved and matches a standard tool's
   result.
2. **Given** a compressed image without checksum files, **When** the user calculates them, **Then**
   both checksum files (compressed file and content) are saved.
3. **Given** a compressed image that has only one of its two checksum files, **When** the user
   presses **Verify image**, **Then** the existing checksum is verified and, if it matches, the
   missing one is saved; if it does not match, nothing is saved and the mismatch is reported.
4. **Given** checksum files are saved, **When** the Restore tab shows the same image, **Then** it
   lists them too.

### Edge Cases

- **Output path equals the source** (e.g. converting `disk.img` to uncompressed): not allowed; the
  create button is unavailable with the reason visible.
- **Output file already exists**: the user is asked before it is replaced, together with its
  checksum files (as for backups).
- **Same size and same format as the source**: nothing to do; the create button is unavailable.
- **Not enough free space** at the output: refused before writing starts when the needed size is
  known (uncompressed output); otherwise the operation stops with the usual "destination full"
  error and leaves no partial file.
- **Destination file system cannot hold files over 4 GB** (FAT32) and the result may be larger:
  the same warning as for backups.
- **Corrupt or truncated compressed image**: conversion and checksum calculation stop with the
  usual error; no result and no checksum files are saved.
- **Cannot write next to the image** (read-only folder or media) when saving checksums: an error
  explains it; the image is unchanged.
- **Cancelling** checksum calculation saves nothing.
- **Image changed or replaced** while the tab shows it: the description and the button label are
  refreshed when the image is selected again or after an operation.

## Requirements *(mandatory)*

### Functional Requirements

**Conversion**

- **FR-001**: The Image tab MUST offer the size choice **Same as the image** in addition to the
  shrink targets, selected by default, and MUST rename the group to "Size of the new image" and
  the button to **Create image**.
- **FR-002**: With **Same as the image**, **Create image** MUST produce a copy of the image's
  content in the chosen output format (compressed or uncompressed) for any image, including
  partition images and images that cannot be shrunk.
- **FR-003**: The converted image MUST have exactly the same uncompressed content as the source.
  When the source has a checksum of its content, the content MUST be checked against it during
  conversion, and a mismatch MUST stop the operation with no result left behind.
- **FR-004**: After conversion the result MUST be verified by reading it back before it is kept,
  as backups with verification are.
- **FR-005**: The result MUST be saved with the same checksum files a backup in that format gets
  (uncompressed: one; compressed: compressed file and content), and a partition image's
  partition information file MUST be copied alongside.
- **FR-006**: When converting to compressed, the fast compression choice MUST be offered, with the
  remembered setting from the Backup tab as default.
- **FR-007**: The suggested output name MUST be the source name with the extension of the chosen
  format (`disk.img.gz` → `disk.img`, `disk.img` → `disk.img.gz`); with a shrink target the
  existing `_shrunk…` names stay.
- **FR-008**: **Create image** MUST be unavailable, with the reason shown, when the output path is
  the source path, or when the size is **Same as the image** and the format equals the source
  format.
- **FR-009**: Conversion MUST never change the source image or its sidecar files and MUST never
  write to a device.

**Checksums**

- **FR-010**: When the selected image has no checksum files, the verify button MUST read
  **Calculate checksums**; pressing it MUST compute the checksums in one pass with progress and
  save them without asking first.
- **FR-011**: When a compressed image has only one of its two checksum files, **Verify image**
  MUST verify the existing one and, if it matches, save the missing one; on a mismatch nothing is
  saved.
- **FR-012**: Saved checksum files MUST use the same names and format as those written by backups,
  so standard tools and the program itself can check them.
- **FR-013**: After checksum files are saved, the Restore and Image tabs MUST show them without the
  image being selected again.

**General**

- **FR-014**: Conversion and checksum calculation MUST use the shared progress panel with
  cancellation, show speed and remaining time, and end with a result message naming the saved
  files.
- **FR-015**: All new and changed texts MUST exist in all six UI languages.

### Key Entities

- **New image request**: source image, size choice (same, smallest, device size, card size),
  output format, fast compression, output path.
- **Checksum files**: next to an image; for an uncompressed image one file (`disk.img.sha256`);
  for a compressed image two (`disk.img.gz.sha256` for the file, `disk.img.sha256` for its
  content).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A converted image decompresses (or is) byte-for-byte identical to the source content
  in 100% of the test images, MBR and GPT, whole-device and partition images.
- **SC-002**: Converting an 8 GB image takes no longer than backing up an 8 GB device to the same
  format on the same machine, minus device read time (the conversion is limited by compression
  and disk speed only).
- **SC-003**: Checksum files produced by the program match a standard SHA-256 tool for 100% of the
  test images.
- **SC-004**: A user can convert an image or add missing checksums in at most three actions after
  selecting the image (choose format, choose or accept name, press the button).
- **SC-005**: No cancelled or failed conversion or checksum calculation leaves a result,
  temporary or checksum file behind.

## Assumptions

- The functions are added to the Image tab from feature 004; no new tab or menu entry.
- Conversion between the two supported image formats is within the constitution's scope (images
  are raw or gzip); no other formats are added.
- The output of a conversion goes next to the source by default; any folder can be chosen.
- "Same as the image" keeps the exact content length; it does not trim unused space after the
  last partition (that is what shrinking is for).
- Calculating checksums only reads the image; it never modifies the image itself.
- The existing question after **Verify image** for images without checksums ("save the computed
  checksums?") is replaced by the direct **Calculate checksums** action.
