# Feature Specification: Editing Files of an ext4 Partition (Edit Mode with a Pending-Changes File)

**Feature Branch**: `010-ext4-writer`

**Created**: 2026-11-08

**Status**: Implemented in code and tests; manual UI and hardware checks (checklist 46–48) pending

**Input**: User description: "In the Files window, files can now be edited. To do that, switch on
edit mode with the **Edit** button. All changes are first written to a diff file. After pressing
**Save**, the changes are applied to the file system and the checksum is updated."

Follow-up (2026-11-08): "When working with a card (device), changes are written to the card at
once and no checksum is calculated."

## Clarifications

### Session 2026-11-08

- Q1: Edit images only, or physical media (USB/SD) too? → A: Both. No unmounting is needed because
  Windows does not mount ext4 partitions, so the tool never has to dismount anything of the card;
  only the sector range of the edited ext4 partition is written.
- Q2: What can be changed? → A: The content of existing files, and also creating, deleting and
  renaming files and directories.
- Q3: What happens to checksums on **Save**? → A: The checksum files that lie next to the image
  are recalculated and rewritten; when there are none, a `.sha256` file is created.
- Q4 (same day, refines Q1): How does editing work on a physical card? → A: Different from an
  image. With a card, changes are written to the card straight away, with no diff file and no
  **Save**/**Discard** step, and no checksum is calculated (a card has no checksum file). The diff
  file, **Save**, **Discard** and the checksum update are for images only.

## Layout Mockup *(approved)*

The **Files** tab of features 008 and 009 gets an **Edit** toggle button to the left of **Tools**.
Switching it on does not touch the image or the card. In edit mode the viewer becomes an editor,
the tree gets a context menu (New file, New folder, Rename, Delete), and two more buttons appear:
**Save** and **Discard**. The status line tells how many changes are waiting.

```text
│ ├──────────────────────────────────┴────────────────────────────────────────────────────┤│
│ │ /etc/hostname · File · -rw-r--r-- · uid 0, gid 0 · 11/5/2026 6:19 PM        EDITING   ││
│ │ ┌─────────────────────────────────────────────────────────────────────────────────┐   ││
│ │ │ raspberrypi-lab                                                                 │   ││
│ │ └─────────────────────────────────────────────────────────────────────────────────┘   ││
│ │ 3 changes not saved           [ Edit ✔ ] [ Discard ] [ Save ] [ Tools ▾ ] [ Extract… ]││
```

Save asks once before it writes, and shows what it is going to do:

```text
┌─ Save changes to raspberry-pi.img ───────────────────────────────────────────────────────┐
│ 3 changes will be written into the image:                                                 │
│   ✎ /etc/hostname              edited (11 → 16 bytes)                                     │
│   ＋ /home/pi/notes.txt        new file (120 bytes)                                       │
│   ✖ /var/log/old.log           deleted                                                    │
│ The checksum file raspberry-pi.img.sha256 will be updated.                                │
│ The image is changed in place. Make a copy first if you may want the old state back.      │
│                                                         [ Save ]  [ Cancel ]              │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

A card in a reader is edited differently (Q4): there is no waiting list, no **Discard** and no
Save dialog. Switching **Edit** on asks once, naming the device (model, size, partition), and then
the status line says "Editing the card: changes are written immediately". Pressing **Save** with a
text file open writes that file to the card at once; creating, renaming and deleting are written as
soon as they are confirmed.

```text
┌─ Edit the card? ─────────────────────────────────────────────────────────────────────────┐
│ SanDisk Ultra 16 GB (USB card reader), partition 2 (ext4, 14.5 GB)                        │
│ Every change you make is written to the card at once and cannot be undone.                │
│ Only the ext4 partition is written; the other partitions of the card are left alone.      │
│                                                         [ Edit the card ]  [ Cancel ]     │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Change a file inside an image and save it (Priority: P1)

A user has a backup of a Raspberry Pi card and wants to change one setting, for example the host
name or a line in a configuration file, without writing the image to a card, booting it and
changing it there. In the **Files** tab they open the image, switch on **Edit**, select the file,
change the text and press **Save**. The image now contains the changed file and nothing else
differs.

**Why this priority**: this is the whole point of the feature and the smallest piece that delivers
value on its own.

**Independent Test**: open a prepared image, edit the text of one file, save; the file reads back
with the new text, every other file, directory, owner, permission and time is unchanged, and the
file system passes an independent check.

**Acceptance Scenarios**:

1. **Given** an opened image in edit mode, **When** the user changes a text file and presses
   **Save**, **Then** the file in the image has the new content, its size and modification time
   are updated, and all other entries of the file system are byte-for-byte as they were.
2. **Given** a change that makes the file longer or shorter (even across a block boundary),
   **When** it is saved, **Then** the file system stays consistent: free space, the free counters
   and the checksums inside the file system are right, so an independent checker finds no problem.
3. **Given** the user has changed a file but not pressed **Save**, **When** the image is looked at
   from outside (hash, other tools), **Then** it is exactly as it was before the first change.

---

### User Story 2 - Changes wait in a diff file until I save them (Priority: P1)

Every change is recorded first in a **diff file** next to the image, not in the image. The user can
close the program, come back, and the waiting changes are still there. They can look at what is
waiting, discard it all, or save it. Until **Save**, the image is not written to in any way.

**Why this priority**: this is what makes editing a backup safe, and it is the user's explicit
requirement.

**Independent Test**: make several changes, close the program, reopen the same image: the same
list of changes is offered and the image's hash is unchanged; **Discard** removes the diff file and
leaves the image as it was.

**Acceptance Scenarios**:

1. **Given** an image with waiting changes, **When** the program is closed and started again and
   the image is opened, **Then** it opens in edit mode with the same changes shown and the status
   line "N changes not saved".
2. **Given** waiting changes, **When** the user presses **Discard** and confirms, **Then** the diff
   file is removed and the tree shows the image as it is.
3. **Given** the image file was changed by something else after the diff was made, **When** it is
   opened or saved, **Then** the program says that the changes no longer belong to this image and
   offers only **Discard**; nothing is written.
4. **Given** waiting changes, **When** the user opens a different image or switches off **Edit**,
   **Then** the program asks whether to keep the changes in the diff file, save or discard them;
   it never drops them silently.

---

### User Story 3 - Create, delete and rename files and folders (Priority: P2)

In edit mode the user can also add a new text file or folder, delete a file or a whole folder, and
rename an entry, from a context menu of the tree. These are recorded in the diff file like content
changes and appear in the tree at once (new entries marked as new, deleted ones struck through).

**Why this priority**: editing content covers most needs; adding, removing and renaming completes
the editor.

**Independent Test**: create a file and a folder, delete another file and a folder with content,
rename an entry, save; the image's tree equals the expected tree and passes the independent check.

**Acceptance Scenarios**:

1. **Given** edit mode, **When** the user creates a new file in a directory and types text,
   **Then** after **Save** the file exists in the image with the text, owned by the same owner as
   its directory, with permissions `rw-r--r--` (directories `rwxr-xr-x`).
2. **Given** a directory with many files, **When** it is deleted and saved, **Then** all its
   entries are gone, and the space they used is free again (the free space of the file system grows
   by what they used).
3. **Given** an entry, **When** it is renamed to a name that already exists in the directory or
   contains a slash or is empty, **Then** the program refuses at once with the reason.
4. **Given** a hard-linked file, **When** one of its names is deleted, **Then** the other names and
   the content stay (the data is freed only with the last name).

---

### User Story 4 - The checksum follows the image (Priority: P2)

After a successful **Save**, the checksum files next to the image are updated, so that **Verify
image** on the Image tab still succeeds. If the image has no checksum file, a `.sha256` file is
created.

**Why this priority**: a changed image with a stale checksum would be reported as corrupt; the
user asked for this explicitly.

**Independent Test**: save a change to an image that has a `.sha256` file; the file now holds the
hash of the saved image and **Verify image** passes; repeat for an image without checksum files.

**Acceptance Scenarios**:

1. **Given** an image with `disk.img.sha256`, **When** the changes are saved, **Then** that file
   holds the new SHA-256 of the whole image, in the same format as before.
2. **Given** an image without checksum files, **When** the changes are saved, **Then** a
   `disk.img.sha256` file is created next to it.
3. **Given** the checksum cannot be written (for example a read-only folder), **When** **Save**
   finishes, **Then** the result says clearly that the image was changed but the checksum is
   stale, and says what to do (calculate it on the Image tab).

---

### User Story 5 - Edit the file system of a card in the reader, written at once (Priority: P3)

The user opens a physical SD card or USB drive (Device source of the Files tab) and edits it. On a
card there is no waiting list: every change is written to the card as soon as it is made, and no
checksum is calculated. The program lists only removable media, asks once when edit mode is switched
on (naming model, size and partition), and writes only into the ext4 partition; the other volumes of
the card (the FAT boot partition of a Raspberry Pi) are not touched, locked or dismounted.

**Why this priority**: it removes the need for an image when a card is at hand, but it carries the
highest risk and is built on the image path.

**Independent Test**: on a test card with an ext4 and a FAT partition, switch on edit mode, edit a
file and press **Save**, create, rename and delete entries; each change is on the card at once, the
FAT partition's bytes are identical before and after, no diff file or checksum file exists, and the
file system passes an independent check after every change.

**Acceptance Scenarios**:

1. **Given** a removable card opened as a device, **When** the user switches on **Edit**, **Then**
   the program checks the card is removable media, not a system disk, not write-protected, explains
   the reason if it refuses, and otherwise asks once, naming the device and saying that changes are
   written immediately and cannot be undone; **Cancel** is the default button.
2. **Given** edit mode on a card with a text file changed in the editor, **When** the user presses
   **Save**, **Then** that file is written to the card at once and the editor shows it as saved; the
   other files are not touched.
3. **Given** edit mode on a card, **When** the user creates, renames or deletes an entry, **Then**
   the change is written to the card as soon as it is confirmed; deleting asks for confirmation
   first and names how much it removes.
4. **Given** edit mode on a card, **When** the user looks for **Discard** or a waiting list,
   **Then** there is none; the status line says "Editing the card: changes are written immediately".
5. **Given** the card is pulled out or a write fails during a change, **When** the failure happens,
   **Then** the program stops with the cause and the offset, says which change was in progress and
   that the file system may need repair, and leaves edit mode.
6. **Given** a change on a card is finished, **Then** no checksum is calculated or written
   anywhere, and no diff file is created.

---

### User Story 6 - A save that was interrupted can be finished (Priority: P2)

Writing into a file system is not atomic. If the power fails or the program is killed during
**Save** of an image, the image may be half-changed. (A card is different: see story 5.) The diff file is kept until the
save has completed; opening the image again says that a save was interrupted and offers to finish
it. Finishing gives the same result as an uninterrupted save.

**Why this priority**: it is the safety net for the one operation that changes the user's data.

**Independent Test**: interrupt a save at many points (a test device that stops writing after N
writes); for every N, saving again produces an image identical to the uninterrupted result.

**Acceptance Scenarios**:

1. **Given** a save interrupted after any number of writes, **When** the image is opened again,
   **Then** the program reports the interrupted save and offers **Finish save** as the only action
   that writes.
2. **Given** an interrupted save, **When** the user presses **Finish save**, **Then** the image
   becomes identical to the one an uninterrupted save would have produced, and the checksum is
   updated.
3. **Given** an interrupted save, **When** the image is browsed before finishing, **Then** the
   tree is shown with a warning that the file system is in the middle of a save.

---

### Edge Cases

- The file system is not cleanly unmounted, has a journal that needs recovery, is damaged or uses
  a feature the program cannot write: **Edit** is refused with the reason (the user can repair the
  image first on the Image tab); browsing still works.
- A compressed `.img.gz` image: **Edit** is refused with the hint to convert it to `.img` first.
- The image is on a read-only medium, is read-only, or is open in another program: the program
  says so and does not enter edit mode.
- The file system has no room for the change (new content, new files): **Save** is refused before
  anything is written, with the needed and free sizes; the diff file stays.
- A file the viewer cannot show as text (binary, not valid UTF-8, larger than the viewer limit):
  it cannot be edited as text; it can still be renamed and deleted.
- A text file with Windows line endings or without a final line break: the editor keeps exactly
  what is in the file; a new file gets Unix line endings.
- Names: longer than 255 bytes, containing a slash or a NUL, empty, `.` or `..`: refused. A name
  with characters Windows cannot store (`a:b`) is allowed in the image.
- A directory deleted that contains the file being edited, or two changes that touch the same
  entry (edit, then rename, then delete): the diff keeps them in order and **Save** gives the same
  result as doing them one by one.
- Symbolic links, device files and other special entries: shown, can be renamed and deleted, their
  target or content cannot be edited.
- Changes to the root directory itself and to `lost+found` are refused.
- Two instances of the program editing the same image: the second one refuses to enter edit mode
  while the first has the diff file open.
- Extraction (feature 008) and the Tools of feature 009 keep working in edit mode; they show the
  image as it is, without the waiting changes, and say so.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The **Files** tab MUST have an **Edit** button that switches edit mode on and off.
  Outside edit mode the tab behaves exactly as before (read-only).
- **FR-002**: Switching on edit mode MUST NOT write anything to the image or the device.
- **FR-003**: Edit mode MUST be refused, with the reason in the user's language, for: a compressed
  image, an image or device that cannot be opened for writing, a write-protected device, a device
  that is not removable media or that hosts the system or the program, and a file system that is
  not cleanly unmounted, is damaged, or has features the program cannot write.
- **FR-004**: In edit mode the user MUST be able to change the text of an existing regular file
  that the viewer shows as text (valid UTF-8, within the viewer's size limit), keeping line
  endings and everything the user did not change.
- **FR-005**: In edit mode the user MUST be able to create a new text file, create a new directory,
  delete a file or a directory with everything in it, and rename a file or directory, from a
  context menu of the tree.
- **FR-006**: When the source is an **image**, every change MUST be recorded in a **diff file** next
  to the image before anything else happens; until **Save** the image MUST remain byte-for-byte
  unchanged. (A card is different, see FR-026 to FR-030.)
- **FR-007**: The diff file of an image MUST hold the ordered list of changes (with the new content) and
  enough to recognise the image it belongs to. When the program is started again and the same image
  is opened, the waiting changes MUST be shown and editing continues from them.
- **FR-008**: The tree and the viewer in edit mode MUST show the image as it would be after
  **Save** (new entries marked, deleted ones struck through, edited text), and the status line MUST
  show the number of changes not saved.
- **FR-009**: For an image, **Discard** MUST remove all waiting changes and the diff file after a confirmation;
  it MUST leave the image untouched.
- **FR-010**: If the image or the partition changed after the diff was made, the program MUST
  refuse to save, say so, and offer only **Discard**.
- **FR-011**: For an image, **Save** MUST show a confirmation listing every change and the checksum
  file that will be updated or created, and say that the image is changed in place; **Cancel** MUST
  be the default button.
- **FR-012**: **Save** (images) and every change on a card MUST check, before writing the first byte
  of that save or change, that it fits (free space, free inodes, names) and refuse with the needed
  and available amounts otherwise.
- **FR-013**: **Save** MUST leave the file system consistent: contents, directory entries, inodes,
  free space and inode accounting, group descriptors, all checksums kept by the file system, and
  the superblock counters MUST be correct, so that an independent file system checker reports no
  error for the result.
- **FR-014**: **Save** MUST change nothing that is not part of a change: entries, owners,
  permissions and times of untouched files, the partition table and every other partition MUST be
  byte-for-byte as before.
- **FR-015**: New files MUST get permissions `rw-r--r--`, new directories `rwxr-xr-x`, the owner
  and group of their parent directory, and the current time; an edited file gets the current
  modification time and keeps owner and permissions.
- **FR-016**: Writes MUST go only to sectors inside the edited ext4 partition; writing outside it
  MUST be impossible (not only avoided).
- **FR-017**: The diff file of an image MUST be kept until **Save** has completed; if **Save** fails or is
  interrupted, the diff file MUST remain and the image MUST report "save interrupted" when it is
  opened again, offering to finish it. Finishing MUST give the result of an uninterrupted save.
- **FR-018**: After a successful **Save** of an image the diff file MUST be removed, and the checksum files
  next to the image (`.sha256`, and any other checksum files that already exist) MUST be
  recalculated for the saved image and rewritten in their existing format; when none exists, a
  `<image>.sha256` file MUST be created.
- **FR-019**: If the checksum files cannot be written, the result MUST say that the image was
  changed and its checksum is stale, and how to fix it; it MUST NOT report plain success.
- **FR-020**: Save MUST show progress and support **Cancel** at any time; cancelling MUST leave the
  state of an interrupted save (FR-017), never a silent half-result.
- **FR-021**: A device MUST be written only through a handle that is opened for that purpose, never
  while another operation of the program uses it; volumes of the card other than the edited
  partition MUST NOT be locked, dismounted or written.
- **FR-026**: When the source is a **card (device)**, switching on **Edit** MUST ask once, naming
  model, size, bus and partition and saying that every change is written immediately and cannot be
  undone; **Cancel** MUST be the default button. Nothing is written before the answer.
- **FR-027**: On a card, no diff file, waiting list, **Discard** or **Save** confirmation exists:
  pressing **Save** with a text file open writes that file to the card at once, and creating,
  renaming and deleting are written as soon as they are confirmed. Deleting MUST ask for
  confirmation first and say how much it removes.
- **FR-028**: On a card, **no checksum is calculated or written**, and no checksum file is created
  or changed anywhere.
- **FR-029**: Each change on a card MUST be written in an order that an interruption (card removed,
  failed write, power loss) leaves the file system either as before the change, as after it, or
  with only unreferenced (leaked) blocks or inodes, which a file system check on Linux removes; it
  MUST NOT leave a directory entry or file that points to unwritten data, and a file's content MUST
  be entirely old or entirely new.
- **FR-030**: If a write to a card fails, the program MUST stop with the cause and the offset, say
  which change was in progress and that the file system may need repair, and leave edit mode.
- **FR-022**: Names MUST be validated at once (not empty, no slash or NUL, not `.` or `..`, at most
  255 bytes, not already in the directory); the reason MUST be shown.
- **FR-023**: Extraction and the Tools of feature 009 MUST keep working and MUST use the image as it
  is on disk; the windows MUST say that waiting changes are not included.
- **FR-024**: All new texts MUST exist in the six languages of the program.
- **FR-025**: The feature MUST work on Windows 7 SP1 through Windows 11 and add no runtime
  dependency.

### Key Entities

- **Edit session**: the state of the Files tab in edit mode for one image or device. For an image:
  the waiting changes, the diff file, and whether a save was interrupted. For a card: only the
  confirmation that changes are written at once.
- **Change**: one entry of the diff: edit content of a file, create a file, create a directory,
  delete an entry, rename an entry; each with the path and the new data it needs.
- **Diff file**: (images only) the file next to the image that holds the changes and the identity of the image
  until they are saved; it never contains anything but the user's own edits and names.
- **Saved image**: the image after **Save**, with its updated or new checksum file.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After a save of edits, creations, deletions and renames on prepared images, an
  independent file system check (`e2fsck -fn`) reports no problem, for 100% of a set of at least 15
  prepared scenarios (growing and shrinking files, files across block boundaries, large
  directories, hard links, many deletions, nearly full file systems).
- **SC-002**: After a save, the content of every file of the image equals the expected content and
  every untouched entry (content, owner, permissions, times) equals the original, for 100% of those
  scenarios.
- **SC-003**: Until **Save** is pressed, the SHA-256 of the image is identical to the one before the
  first change, in every scenario, including after closing and reopening the program.
- **SC-004**: A save interrupted after any number of writes (tested at every write of at least five
  scenarios) and then finished gives an image identical to the uninterrupted result in 100% of
  cases, and no interrupted save is ever reported as complete.
- **SC-005**: After a save, the checksum file equals the SHA-256 of the saved image and **Verify
  image** succeeds, with and without a checksum file before the save.
- **SC-006**: On a card with an ext4 and a FAT partition, the bytes of the FAT partition and of the
  partition table are identical before and after any change, and no write was attempted outside the
  ext4 partition (verified by a recording device in tests).
- **SC-009**: Working on a card creates no diff file and no checksum file anywhere, calculates no
  checksum, and after every single change the file system passes an independent check (at least ten
  changes of every kind).
- **SC-010**: A change on a card interrupted after any number of writes (tested at every write of at
  least five scenarios) leaves the file system as before, as after, or with only leaked blocks or
  inodes, never with an entry that points to unwritten data or a torn file.
- **SC-007**: Writing the changes of a save into an image takes under 30 seconds for an image of
  any size up to 32 GiB (no copy of the image is made); recalculating the checksum afterwards reads
  the whole image, takes as long as verifying it, shows progress and can be cancelled (the result
  then says the checksum is stale). On a card a single change is written in under 5 seconds.
- **SC-008**: Every refusal (compressed image, damaged file system, write-protected device, not
  enough space, changed image) shows a reason in the user's language and writes nothing.

## Assumptions

- The constitution must be amended before planning: Principle VII (scope: browsing and extraction
  are read-only and "file-level backup that writes into images or devices" needs an amendment) and
  Principle I (writing into images; writing a card's partition without dismounting volumes) are
  extended to cover **editing files of the ext4 file system of an image or a removable device**,
  with the safeguards above (diff file, confirmation, no write outside the partition, refusal on a
  file system that is not clean, finishing an interrupted save). This is a MINOR amendment (1.5.0)
  and is applied before `/speckit-plan`.
- Only ext4 partitions are edited; the same file system features that feature 008 can read are
  needed, restricted to those the writer supports (the list is fixed in the plan and everything
  else is refused with the reason, as the shrink and repair features do).
- The file system is written to as it is on disk; the journal is not replayed and is not used for
  the changes (a file system that needs journal recovery must be repaired first on the Image tab).
- The diff file of an image lies next to the image with the image's name plus a fixed extension.
  A card has no diff file at all (Q4).
- The editor is for text of up to the viewer's limit (1 MiB); larger or binary files can be renamed
  and deleted but not edited.
- Only the checksum files that sit next to the image (`.sha256`, and the other formats feature 005
  writes) are updated; a `.sha256` file is created when none exists. For a card no checksum is
  calculated or stored (Q4).
- File times are the current time of the computer; ownership of new entries is taken from the
  parent directory.
- No undo history inside the editor beyond what a text box gives; for an image the diff file is the
  record of what is waiting; on a card there is no undo once a change is written.
- Files and directories bigger than the free space cannot be created; there is no resizing of the
  file system.
