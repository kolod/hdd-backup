# Feature Specification: Browse and Extract Files from an ext4 Partition

**Feature Branch**: `008-ext4-browser`

**Created**: 2026-11-05

**Status**: Implemented (layout approved 2026-11-05; Windows 7 and real-device checks pending, see the manual hardware checklist)

**Input**: User description: "A new tab that opens an image or a physical medium and shows the
contents of an ext4 partition. The window is split in two: on the left the file system tree, on the
right a file viewer. The user can extract a file or a directory from the image."

## Clarifications

### Session 2026-11-05

- Q1: What does the right pane show after a selection in the tree? → A: Only the content of the
  selected file. The tree contains directories and files; the right pane has no directory listing.
- Q2: How are compressed `.img.gz` images handled? → A: Not opened directly. The tab asks the user
  to convert the image to `.img` on the Image tab first; no temporary files are created.

## Layout Mockup *(approved)*

A fourth tab, **Files**, after **Image**. It reads only; nothing on it writes to the image or the
device. Legend: `*` marks a control whose explanation is shown as a tooltip. The menu and the
progress panel are unchanged and drawn only for orientation.

```text
┌─ HddBackup ─────────────────────────────────────────────────────────────────────────────┐
│ Language   Help                                                                         │
│ ┌─────────┬─────────┬───────┬───────┐                                                   │
│ │ Back up │ Restore │ Image │ Files │                                                   │
│ ├─────────┴─────────┴───────┴───────┴───────────────────────────────────────────────────┐│
│ │ Source:  (•) Image file  [ D:\Images\hmi.img                        ] [ Browse… ]     ││
│ │          ( ) Device      [ JetFlash Transcend 32GB (#1)           ▼ ]                 ││
│ │ Partition: [ #2 rootfs (ext4) 28.9 GiB ▼ ]   Clean *                                  ││
│ ├──────────────────────────────────┬────────────────────────────────────────────────────┤│
│ │ ▾ /                              │ /etc/hostname                                      ││
│ │   ▸ bin                          │ 12 bytes · -rw-r--r-- · uid 0, gid 0 · 2026-03-02  ││
│ │   ▸ boot                         │ ┌────────────────────────────────────────────────┐ ││
│ │   ▾ etc                          │ │ raspberrypi                                    │ ││
│ │     ▸ apt                        │ │                                                │ ││
│ │     ▸ network                    │ │                                                │ ││
│ │       fstab                      │ │                                                │ ││
│ │       hostname                   │ │                                                │ ││
│ │       passwd                     │ │                                                │ ││
│ │   ▸ home                         │ └────────────────────────────────────────────────┘ ││
│ │   ▸ var                          │ ( ) Text  (•) Auto  ( ) Hex *                      ││
│ │                                  │                                                    ││
│ ├──────────────────────────────────┴────────────────────────────────────────────────────┤│
│ │ 4 183 files, 612 directories                                      [ Extract… ] *      ││
│ └───────────────────────────────────────────────────────────────────────────────────────┘│
│  Progress panel (as on the other tabs; used while extracting)                            │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Source** is either an image file or a device (same list of USB/SD devices as on the Back up
  tab). The image is the one shared with the Restore and Image tabs (feature 004).
- **Partition** lists the ext4 partitions found in the source. A single-partition image opens
  directly. The status next to it is "Clean", or a warning (see User Story 5).
- **Tree** (left): directories and files; directories open on demand. Symbolic links show their
  target; special files (devices, sockets, pipes) are shown but cannot be viewed or extracted.
- **Viewer** (right): a header line with the path, size, permissions, owner and modification time,
  then the beginning of the file as text or as a hex dump. Nothing is shown for a directory except
  its header line.
- **Extract…** is enabled when a file or directory is selected (also in the tree's context menu).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse the file system of an image (Priority: P1)

A user has a backup image of a single-board computer card and wants to see what is inside without
writing the image to a card. On the **Files** tab they choose the image, pick the Linux (ext4)
partition if there is more than one, and see the file system as a tree they can expand and
collapse.

**Why this priority**: everything else depends on being able to open a source and see its tree;
alone it already answers "what is in this backup?".

**Independent Test**: open a fixture image with a known tree; expand directories; compare the
names, types and sizes shown with the known contents.

**Acceptance Scenarios**:

1. **Given** an `.img` image with an ext4 partition, **When** the user selects it on the Files tab,
   **Then** the partition is selected (or offered if there are several) and the tree shows the root
   directory with its entries.
2. **Given** the tree, **When** the user expands a directory, **Then** its entries appear in
   alphabetical order, directories first, without delay noticeable on a directory of thousands of
   entries.
3. **Given** a single-partition image (from a partition backup), **When** it is selected, **Then**
   it opens directly with no partition choice.
4. **Given** an image with no ext4 partition, **When** it is selected, **Then** the tab says that
   no ext4 partition was found and lists the partitions that are present.
5. **Given** a `.img.gz` image, **When** it is selected, **Then** the tab explains that compressed
   images must first be converted to `.img` on the Image tab, and creates no file.
6. **Given** an image selected on the Restore or Image tab, **When** the user opens the Files tab,
   **Then** the same image is already selected there.

---

### User Story 2 - Extract a file or a directory (Priority: P1)

The user selects a file or a directory in the tree, presses **Extract…**, chooses a destination
folder on the computer and gets a copy of it there: one file, or the whole directory with all its
subdirectories and files.

**Why this priority**: this is the point of the feature — getting a configuration file or a
project folder out of a backup without restoring the whole card.

**Independent Test**: extract a file and a directory tree from a fixture image and compare each
extracted file byte for byte (SHA-256) with the content recorded for the fixture.

**Acceptance Scenarios**:

1. **Given** a selected file, **When** the user extracts it to a folder, **Then** a file with the
   same name, content and modification time exists in that folder.
2. **Given** a selected directory, **When** the user extracts it, **Then** the directory and
   everything below it are recreated in the destination with the same names, content and
   modification times, and progress is shown.
3. **Given** an extraction in progress, **When** the user cancels, **Then** it stops promptly and
   no partly written file is left that looks complete.
4. **Given** a destination that already contains files of the same names, **When** the user starts
   extraction, **Then** before anything is written the user is told how many files would be
   replaced and chooses to replace them, to skip them or to cancel; nothing is replaced silently.
5. **Given** a destination with less free space than the selection needs, **When** the user starts
   extraction, **Then** it is refused before anything is written, with the needed and the
   available sizes.
6. **Given** extraction finished, **When** some items could not be extracted or had to be renamed,
   **Then** a summary lists each of them with the reason; items that were extracted are not
   affected.

---

### User Story 3 - View the content of a file (Priority: P2)

When the user selects a file, the right pane shows its beginning, so the user can check that it is
the file they are looking for before extracting it. Text files are shown as text; other files as a
hex dump with offsets.

**Why this priority**: it saves extracting files just to look at them, but the feature is useful
without it.

**Independent Test**: select a text file, a binary file and an empty file in a fixture and check
what the viewer shows for each.

**Acceptance Scenarios**:

1. **Given** a text file, **When** it is selected, **Then** the viewer shows its text readably,
   including non-ASCII characters in UTF-8.
2. **Given** a binary file, **When** it is selected in Auto mode, **Then** the viewer shows a hex
   dump (offset, bytes, printable characters).
3. **Given** a file larger than the viewer's limit, **When** it is selected, **Then** the viewer
   shows the first part and says how much of the file is shown and its full size.
4. **Given** a directory, a special file or a symbolic link, **When** it is selected, **Then** the
   viewer shows only the header line (for a link, its target) and no content.
5. **Given** the user switches between Text, Auto and Hex, **When** a file is selected, **Then**
   the same bytes are shown in the chosen way.

---

### User Story 4 - Browse a physical medium (Priority: P2)

The user connects a card or a USB drive, chooses **Device** as the source, picks the device and the
ext4 partition, and browses and extracts exactly as with an image. The device is only read.

**Why this priority**: it avoids creating an image just to fetch a few files, but the same result
is possible with an image, so it follows the image workflow.

**Independent Test**: open a file-backed fake device with the same fixture as in User Story 1 and
check that the tree is identical and that no write request reaches the device.

**Acceptance Scenarios**:

1. **Given** a connected USB or SD device with an ext4 partition, **When** the user chooses it,
   **Then** its partitions are listed and the tree is shown as for an image.
2. **Given** a device being browsed, **When** the user views or extracts files, **Then** nothing is
   written to the device and no confirmation for writing is requested.
3. **Given** a device being browsed, **When** it is removed or fails to read, **Then** the tab says
   so, the tree is cleared, and extraction in progress stops with the cause reported.
4. **Given** a device being browsed, **When** the user chooses a destination on a volume of the
   same device, **Then** extraction is refused with an explanation.

---

### User Story 5 - Damaged or not cleanly unmounted file systems (Priority: P3)

Cards from devices that lost power often hold file systems that were not unmounted cleanly. The tab
still opens them, tells the user about the state, and keeps working around entries it cannot read.

**Why this priority**: it matters for the main use case (cards of single-board computers) but only
refines behavior that already works for healthy file systems.

**Independent Test**: open the fixtures used for the repair feature (journal needing recovery,
orphan inodes, structural damage) and check the status and that readable parts can still be browsed.

**Acceptance Scenarios**:

1. **Given** a file system that was not cleanly unmounted, **When** it is opened, **Then** a
   warning says that recent changes may be missing or inconsistent and refers to the repair option
   on the Image tab; the contents are shown as they are on disk, and the image is not changed.
2. **Given** a directory or a file whose metadata cannot be read, **When** the user selects it,
   **Then** that item is marked as unreadable with the reason, the rest of the tree stays usable,
   and extraction reports the item and continues with the others.
3. **Given** a partition that is not an ext4 file system (including an ext4 that is too damaged to
   find its root), **When** it is chosen, **Then** the tab says so and shows no tree.

---

### Edge Cases

- Directory with a very large number of entries (tens of thousands): the tree stays responsive.
- File names that Windows cannot store (reserved characters, names such as `CON`, trailing dots or
  spaces) or names that differ only by letter case in one directory: extracted under changed names
  that are predictable and are listed in the summary; no file overwrites another.
- Paths that are too long for Windows: reported individually in the summary, never skipped
  silently.
- Symbolic links, hard links, device files, sockets and pipes during extraction: symbolic links
  and special files are not created and are listed in the summary; a hard-linked file is
  extracted as an independent copy for each name.
- Sparse files and very large files (larger than the destination's file size limit, for example on
  FAT32): reported with the reason; extraction continues with the other items.
- Files with unusual attributes (encrypted content, content stored in a way the program does not
  support): marked as not viewable or extractable with the reason, never shown as garbage.
- The image is replaced or deleted while it is open: reading errors are reported, not shown as an
  empty tree.
- The destination is the image file itself or inside the device being browsed: refused.
- The user changes the source or partition while an extraction runs: the change waits or is
  refused until extraction ends or is cancelled.
- A partition with a block size or feature set that the program does not support: reported with the
  feature name; no tree is shown.

## Requirements *(mandatory)*

### Functional Requirements

**Source and partitions**

- **FR-001**: The application MUST provide a **Files** tab, after the Image tab, on which the user
  chooses a source: an image file or a device.
- **FR-002**: The image source MUST accept uncompressed `.img` images, both whole-device images and
  single-partition images. For a compressed `.img.gz` image the tab MUST explain that it has to be
  converted to `.img` on the Image tab first, and MUST NOT create a temporary file.
- **FR-003**: The device source MUST offer the same set of devices as the Back up tab (removable
  USB and SD media only) and MUST open the chosen device for reading only.
- **FR-004**: The tab MUST list the ext4 partitions found in the source, let the user choose one,
  and open it directly when there is exactly one; for a source with no ext4 partition it MUST say
  so and list the partitions present.
- **FR-005**: The image selected on the Restore and Image tabs MUST be the image selected on the
  Files tab and vice versa.

**Tree**

- **FR-006**: The left pane MUST show the file system as a tree of directories and files, starting
  at the root directory; directories MUST be expanded on demand, and the tree MUST stay responsive
  for directories with tens of thousands of entries.
- **FR-007**: Entries MUST be sorted with directories first, then alphabetically; symbolic links
  MUST show their target; special files MUST be shown and marked as such.
- **FR-008**: The tab MUST show the number of files and directories of the opened file system, or a
  progress indication while it is being counted.

**Viewer**

- **FR-009**: The right pane MUST show, for the selected item, a header line with its full path,
  type, size, permissions, owner and group (numbers) and modification time.
- **FR-010**: For a regular file, the right pane MUST show its content from the beginning, as text
  or as a hex dump, chosen automatically by default and switchable by the user between Text, Auto
  and Hex; a file larger than the viewer's limit MUST show the first part together with the amount
  shown and the full size.
- **FR-011**: For any other item (directory, symbolic link, special file) the right pane MUST show
  only the header line, with the target for a symbolic link.

**Extraction**

- **FR-012**: The user MUST be able to extract the selected file or directory (with all its
  contents) to a folder of their choice, from a button and from the tree's context menu.
- **FR-013**: Extracted regular files MUST have exactly the content they have in the image and the
  same modification time; directories MUST keep their modification times.
- **FR-014**: Extraction MUST show progress (items and bytes, speed, elapsed and remaining time) in
  the shared progress panel, and MUST be cancellable at any time; cancelling or an error MUST NOT
  leave a partly written file that looks complete.
- **FR-015**: Extraction MUST NOT replace an existing file without asking; before writing anything
  it MUST report how many existing files would be replaced and let the user replace them, skip
  them or cancel (one choice for all conflicts).
- **FR-016**: Before writing, extraction MUST compare the required size with the free space of the
  destination and refuse to start, with both sizes shown, if it does not fit.
- **FR-017**: Names that cannot be stored on Windows MUST be changed in a predictable way that never
  makes two items collide; symbolic links and special files MUST NOT be created; hard-linked files
  MUST be extracted as independent copies.
- **FR-018**: When extraction ends, the application MUST show a summary with the number of items
  and bytes extracted and a list of every item that was skipped, renamed or failed, each with the
  reason; one failing item MUST NOT stop the extraction of the others.

**Safety (Principle I)**

- **FR-019**: Browsing, viewing and extracting MUST never write to the image or to the device, and
  MUST never need a write confirmation; an extraction destination that is the image file itself
  or a volume of the device being browsed MUST be refused.
- **FR-020**: A file system that was not cleanly unmounted MUST be opened as it is on disk, with a
  visible warning that recent changes may be missing or inconsistent and a pointer to the repair
  option; the program MUST NOT replay the journal or change anything in the source.
- **FR-021**: Unreadable or damaged entries MUST be marked as such with the reason; the rest of the
  tree and extraction of the other items MUST continue working.
- **FR-022**: Read errors from the source (removed device, I/O error, vanished image) MUST be
  reported with the cause and the offset, never shown as empty or partial content.

**Platform**

- **FR-023**: All new text (labels, tooltips, messages, the extraction summary) MUST come from the
  resource tables in all six supported languages, and the layout MUST not clip text in any of
  them.
- **FR-024**: The tab MUST work on Windows 7 SP1 through Windows 11, in the single executable.

### Key Entities

- **Source**: the image file or the device being browsed; read-only; has partitions.
- **ext4 partition**: one file system chosen from the source; has a state (clean, not cleanly
  unmounted, damaged, unsupported) and a root directory.
- **Entry**: a directory, regular file, symbolic link or special file in the tree; has a name, full
  path, type, size, permissions, owner, group and modification time, and may be unreadable.
- **Extraction**: copying an entry (and everything below it) to a destination folder; has progress,
  conflict decisions and a summary of skipped, renamed and failed items.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For an image or device of up to 32 GiB, the root of the file system is shown within
  5 seconds of choosing the source, regardless of its size.
- **SC-002**: Expanding a directory of 10 000 entries, or selecting a file and seeing its content,
  takes under 2 seconds on a USB 2.0 card reader.
- **SC-003**: Every regular file extracted from the test images is byte-identical (same SHA-256) to
  the content that an independent Linux file system tool reads from the same image; modification
  times match to the second.
- **SC-004**: After any browsing, viewing or extraction session, the SHA-256 of the image is
  unchanged, and tests with a fake device observe zero write requests.
- **SC-005**: Cancelling an extraction stops it within 2 seconds and leaves no partly written file
  that looks complete.
- **SC-006**: A first-time user can open an image, find a given file and extract it in under one
  minute without documentation.
- **SC-007**: In every one of the six languages, the tab shows no clipped text at 100% display
  scaling on Windows 11, as checked for the existing tabs.
- **SC-008**: The images used for the repair feature (journal needing recovery, orphan inodes,
  structural damage) all open without a crash, with the correct warning, and their readable files
  can be extracted.

## Assumptions

- **Scope amendment**: Principle VII of the constitution excluded file-level backup. This feature
  only reads and extracts individual files from an existing image; it does not create file-level
  backups, modify the image or write to the device. The MINOR amendment (1.3.0) that widens
  Principle VII accordingly was applied on 2026-11-05, when implementation started.
- Only ext4 is supported; other file systems of the source (for example the FAT boot partition of
  a Raspberry Pi card) are listed but not browsable. ext2 and ext3 are out of scope unless the
  existing ext4 reader already handles them identically.
- The tree is a snapshot taken when the partition is opened; if the source changes underneath it
  (a mounted device written by another program), the user reopens the partition.
- Compressed images are converted with the existing conversion on the Image tab (feature 005);
  this feature adds no decompression of its own and no temporary files.
- Extraction keeps content and modification times only. Ownership, permissions, extended
  attributes and ACLs cannot be represented on Windows and are not recreated; the viewer header
  still shows owner and permissions.
- The viewer shows a bounded amount of a file (the beginning only, on the order of a megabyte);
  searching in the file, editing, opening files in other programs and previewing images are out of
  scope. Extraction is the way to get a whole file.
- Out of scope: modifying, deleting, adding or renaming files in an image; searching across the
  file system; drag and drop to Windows Explorer; browsing several partitions at once; opening
  `.img.gz` directly.
- Opening a device needs administrator rights, which the application already requires.
- Depends on the existing ext4 reader, the shared selected image (feature 004), the progress panel,
  the conversion on the Image tab (feature 005) and the repair option (feature 006).
