# Feature Specification: Two-Column Layout and an Image Tab

**Feature Branch**: `004-two-column-layout`

**Created**: 2026-10-15

**Status**: Draft (layout approved 2026-10-15)

**Input**: User description: "UI: backup/restore settings in a column on the right; the source
device for backup/restore on the left; the image path at the bottom; move the text in parentheses
into tooltips; progress and the start button as now; draw the layout in pseudographics before it
is approved."

## Clarifications

### Session 2026-10-15

- Q: Layout of the Backup and Restore tabs? → A: Approved as drawn below (device lists on the
  left, options in a right column, image path and start button at the bottom).
- Q: Functions that do not read or write a device (verify image, shrink image)? → A: Move them to
  a separate **Image** tab. The standalone "Shrink image" dialog is replaced by that tab, and the
  shrink target can be taken from the size of a connected device.
- Q: Is the selected image shared between the Restore and Image tabs? → A: Yes, one shared image.
- Q: Same feature or a separate one? → A: Part of this feature (004).

## Layout Mockups *(approved)*

Legend: `*` marks a control whose explanation (formerly in parentheses) is shown as a tooltip.
The menu, the tab headers and the progress panel are unchanged except for the new **Image** tab
header; they are drawn only for orientation.

### Backup tab

```text
┌─ HddBackup ─────────────────────────────────────────────────────────────────────────────┐
│ File   Language   Help                                                                  │
│ ┌────────┬─────────┬───────┐                                                            │
│ │ Backup │ Restore │ Image │                                                            │
│ ├────────┴─────────┴───────┴───────────────────────────────────────────────────────────┐│
│ │ Source device:                                       │ Backup scope                  ││
│ │ ┌──────────────────────────────────────────────────┐ │ (•) Whole device *            ││
│ │ │ Device              Size     Bus  Volumes  Table │ │ ( ) Single partition          ││
│ │ │ Generic SD Reader   14.8 GB  USB  E:, F:   MBR   │ │                               ││
│ │ │ SanDisk Cruzer      29.3 GB  USB  G:       GPT   │ │ Format                        ││
│ │ │                                                  │ │ [ Compressed *       ▼]       ││
│ │ │                                                  │ │ [ ] Fast compression *        ││
│ │ └──────────────────────────────────────────────────┘ │                               ││
│ │ ⚠ The selected reader cannot be used … (if any)      │ After backup                  ││
│ │ [ ] Back up anyway *                     (if any)    │ [x] Verify the image file     ││
│ │ Partitions:                    (Single partition)    │     after backup              ││
│ │ ┌──────────────────────────────────────────────────┐ │ [x] Eject the device when     ││
│ │ │ #  Name     File system  Size      Offset *      │ │     finished                  ││
│ │ │ 1  boot     FAT32        512 MB    4 194 304     │ │                               ││
│ │ │ 2  rootfs   ext4         14.3 GB   541 065 216   │ │                               ││
│ │ └──────────────────────────────────────────────────┘ │                               ││
│ ├──────────────────────────────────────────────────────┴───────────────────────────────┤│
│ │ Save image as:                                                                       ││
│ │ [ D:\Images\Generic_SD_Reader_2026-10-15.img.gz                      ] [ Browse… ]   ││
│ │                                                                    [ Start backup ]  ││
│ └──────────────────────────────────────────────────────────────────────────────────────┘│
│ ┌─ progress panel (unchanged; shown during and after an operation) ────────────────────┐│
│ │ Reading device…  ████████████░░░░░░░░░░░░  48%   Speed: 21 MB/s (average 20 MB/s)    ││
│ └──────────────────────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

### Restore tab

```text
│ ├────────┴─────────┴───────┴───────────────────────────────────────────────────────────┐│
│ │ Target device:                                       │ After writing                 ││
│ │ ┌──────────────────────────────────────────────────┐ │ [x] Read back and verify      ││
│ │ │ Device              Size     Bus  Volumes  Table │ │     after writing             ││
│ │ │ Generic SD Reader   14.8 GB  USB  E:, F:   MBR   │ │ [x] Eject the device when     ││
│ │ │                                                  │ │     finished                  ││
│ │ └──────────────────────────────────────────────────┘ │                               ││
│ │ ⚠ The selected reader cannot be used … (if any)      │                               ││
│ │ Target partition:          (partition images only)   │                               ││
│ │ ┌──────────────────────────────────────────────────┐ │                               ││
│ │ │ #  Name     File system  Size      Offset *      │ │                               ││
│ │ └──────────────────────────────────────────────────┘ │                               ││
│ ├──────────────────────────────────────────────────────┴───────────────────────────────┤│
│ │ Image file:                                                                          ││
│ │ [ D:\Images\raspios.img.gz                                           ] [ Browse… ]   ││
│ │ Compressed image, 3.9 GB, whole device                                               ││
│ │ Checksum files: raspios.img.gz.sha256, raspios.img.sha256                            ││
│ │ ⚠ The image is larger than the device. It will be shrunk to fit before writing.      ││
│ │                                                                [ Write to device… ]  ││
│ └──────────────────────────────────────────────────────────────────────────────────────┘│
```

### Image tab (new)

```text
│ ├────────┴─────────┴───────┴───────────────────────────────────────────────────────────┐│
│ │ Image file:                                          │ Shrink to                     ││
│ │ [ D:\Images\raspios.img.gz             ] [ Browse… ] │ (•) Smallest possible         ││
│ │ Compressed image, 3.9 GB, whole device               │ ( ) Size of a device:         ││
│ │ Checksum files: raspios.img.gz.sha256, …             │     [ Generic SD 14.8 GB ▼]   ││
│ │                                                      │ ( ) Card size:                ││
│ │ Shrink analysis                                      │     [ 8 ] [ GiB ▼]            ││
│ │ Partitions: boot FAT32 512 MB, rootfs ext4 14.3 GB   │                               ││
│ │ Used in the Linux (ext4) partition: 3.1 GB           │ Output format                 ││
│ │ Smallest possible image: 3.6 GB                      │ [ Uncompressed *     ▼]       ││
│ │ ⚠ reasons, if the image cannot be shrunk             │                               ││
│ ├──────────────────────────────────────────────────────┴───────────────────────────────┤│
│ │ Save the smaller image as:                                                           ││
│ │ [ D:\Images\raspios_14.8GB.img                                       ] [ Browse… ]   ││
│ │                                          [ Verify image ] [ Create smaller image ]   ││
│ └──────────────────────────────────────────────────────────────────────────────────────┘│
```

### Tooltips (labels shortened, explanation moved)

| Control | Visible label (English) | Tooltip (English) |
|---------|-------------------------|-------------------|
| Backup scope, first option | Whole device | Partition table, boot sectors and all partitions |
| Format list (Backup and Image tabs), item 1 | Compressed | Gzip-compressed image, .img.gz |
| Format list (Backup and Image tabs), item 2 | Uncompressed | Raw image, .img |
| Fast compression | Fast compression | Larger file |
| Back up anyway | Back up anyway | Incomplete image: the reader hides the end of the card |
| Partition list column | Offset | In bytes |

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Choose the device on the left, the options on the right (Priority: P1)

A user opens the Backup or Restore tab. The device list fills the left part of the tab, and every
option of the operation (backup scope, format, compression, verification, eject) is grouped in a
column on the right. The user picks a device on the left, glances at the options on the right,
and does not have to scroll or search for a setting below the device list.

**Why this priority**: this is the core of the request; it changes the reading order of both
tabs and gives the device list the full height of the tab.

**Independent Test**: open each tab at the default window size and check that the device list
is on the left, all options are in the right column, and the device list is taller than before.

**Acceptance Scenarios**:

1. **Given** the Backup tab is open, **When** the user looks at it, **Then** the source device
   list and (for a single-partition backup) the partition list are on the left, and the backup
   scope, format, fast compression, verify and eject options are in the right column.
2. **Given** the Restore tab is open, **When** the user looks at it, **Then** the target device
   list and (for a partition image) the target partition list are on the left, and the verify
   and eject options are in the right column.
3. **Given** a device whose reader misreports its capacity is selected, **When** the tab shows
   the "unsuitable reader" warning (and, on the Backup tab, the "Back up anyway" check box),
   **Then** they appear directly under the device list on the left.
4. **Given** any option is changed, **When** the operation starts, **Then** it behaves exactly as
   it did with the old layout (same settings remembered, same enabling rules).

---

### User Story 2 - Image path at the bottom, start button and progress as now (Priority: P1)

The image file path (with **Browse…**) spans the full width at the bottom of the Backup and
Restore tabs, directly above the start button. On the Restore tab the image description and
checksum files are shown under the path. The start button stays at the bottom right, and the
progress panel stays below the tabs as it is now.

**Why this priority**: the path is the last thing chosen before starting; putting it next to the
start button makes the flow top-to-bottom: device → options → file → start.

**Independent Test**: on each tab, choose a device, options and a path, then start; check that
the path row is full width at the bottom, the start button is at the bottom right, and progress
shows as before.

**Acceptance Scenarios**:

1. **Given** the Backup tab, **When** the user looks at the bottom, **Then** "Save image as:",
   the path box with **Browse…**, and **Start backup** at the bottom right are shown in that
   order.
2. **Given** the Restore tab with an image chosen, **When** the user looks at the bottom,
   **Then** "Image file:", the path box with **Browse…**, the image description and checksum
   files, and **Write to device…** at the bottom right are shown in that order.
3. **Given** an operation is running, **When** the user looks at the window, **Then** the
   progress panel looks and behaves as before.

---

### User Story 3 - Image tab for work that does not touch a device (Priority: P1)

A user wants to check an image or make a smaller copy of it without writing to a card. They open
the **Image** tab, which shows the selected image, its description and checksum files, and the
shrink analysis (partitions, data used in the Linux partition, smallest possible size, or the
reasons why it cannot be shrunk). On the right they choose the target size: the smallest
possible, the size of one of the connected devices, or a card size typed in. They choose the
output format and file name at the bottom and press **Create smaller image**, or press **Verify
image** to check the image against its checksum files.

**Why this priority**: it removes the file-only actions from the Restore tab and replaces the
separate Shrink dialog, so each tab has one purpose; it also adds "fit the size of a connected
device", which the user asked for.

**Independent Test**: with no image write involved, open the Image tab, select a Raspberry Pi
OS-like image, create a smaller copy sized to a connected device, and verify the original image.

**Acceptance Scenarios**:

1. **Given** a full-device image with a shrinkable ext4 last partition, **When** the user opens
   the Image tab, **Then** the shrink analysis shows the used data and the smallest possible
   size, and **Create smaller image** is available.
2. **Given** connected devices, **When** the user chooses "Size of a device", **Then** the list
   offers every listed device with its size, and the smaller image is created to fit that size;
   nothing is written to the device.
3. **Given** "Card size" is chosen, **When** the user enters a size, **Then** the smaller image is
   created to fit that size.
4. **Given** a partition image or an image that cannot be shrunk, **When** the Image tab shows
   it, **Then** the shrink controls are unavailable and the reasons are shown; **Verify image**
   still works.
5. **Given** any image, **When** the user presses **Verify image**, **Then** it is verified with
   the same progress and result as the old **Verify image** button on the Restore tab.
6. **Given** a smaller image was created, **When** the operation completes, **Then** the result
   panel shows where it was saved, as it does today.

---

### User Story 4 - One image shared by the Restore and Image tabs (Priority: P2)

The user selects an image once; it is the selected image on both the Restore and the Image tab.
After creating a smaller image, the user can switch to Restore and write it, or keep the original
selected.

**Why this priority**: saves re-selecting the same file when moving between checking, shrinking
and writing an image.

**Independent Test**: select an image on the Restore tab, switch to the Image tab, and check that
the same image and description are shown; change it there and check the Restore tab.

**Acceptance Scenarios**:

1. **Given** an image selected on the Restore tab, **When** the user opens the Image tab,
   **Then** the same image path, description and checksum files are shown, and vice versa.
2. **Given** checksum files were saved by an operation, **When** either tab is shown, **Then** it
   shows the updated checksum files.

---

### User Story 5 - Early warning when the image is larger than the device (Priority: P2)

On the Restore tab, as soon as both an image and a target device are selected and the image is
larger than the device, a warning under the image description says the image is larger than the
device and will be shrunk to fit before writing (or that it cannot be written, with the reason,
when it cannot be shrunk).

**Why this priority**: the user learns about the size mismatch before pressing the button; the
"Shrink and write" offer after pressing it stays as it is.

**Independent Test**: select a 16 GB image and an 8 GB device and check the warning appears;
select a 32 GB device and check it disappears.

**Acceptance Scenarios**:

1. **Given** an image whose required size exceeds the selected device, **When** both are
   selected, **Then** the warning is shown under the image description.
2. **Given** the warning is shown, **When** the user presses **Write to device…**, **Then** the
   existing "Shrink and write" offer appears and works as before.
3. **Given** the image fits the device, **When** both are selected, **Then** no warning is shown.

---

### User Story 6 - Short labels with explanations in tooltips (Priority: P3)

Labels that carried an explanation in parentheses are shortened to their main words. The
explanation appears as a tooltip when the pointer rests on the control.

**Why this priority**: shorter labels make the right column narrow enough to fit next to the
device list, but they only matter once the new layout exists.

**Independent Test**: hover over each control in the Tooltips table and check that the short
label is shown and the tooltip contains the former explanation, in every UI language.

**Acceptance Scenarios**:

1. **Given** the Backup tab, **When** the user hovers over "Whole device", **Then** a tooltip
   explains that it includes the partition table, boot sectors and all partitions.
2. **Given** a format list, **When** the user hovers over it, **Then** the tooltip explains the
   selected format and its file extension.
3. **Given** the UI language is switched, **When** the user hovers over a control again, **Then**
   the tooltip is in the new language.

### Edge Cases

- **Long translations** (German, French) and **Japanese fonts**: the right column must still
  show every option fully, wrapping long check-box texts onto a second line instead of cutting
  them off or pushing the device list out of view.
- **Minimum window size**: at the smallest allowed window size the device name and size columns
  of the device list stay visible and the right column is not clipped.
- **High DPI** (125%, 150%, 200%): the two columns scale together; nothing overlaps.
- **Hidden rows**: when the unsuitable-reader warning, "Back up anyway", the size warning or the
  partition list is hidden, the remaining controls move up without leaving gaps.
- **No image selected**: the Restore and Image tabs show "Select an image file."; their action
  buttons keep the current enabling rules (disabled without an image).
- **No devices connected**: on the Image tab, "Size of a device" is unavailable; the other
  targets still work.
- **Device removed** while it is chosen under "Size of a device": the choice is cleared and the
  target falls back to "Smallest possible".
- **Target below the smallest possible size**: **Create smaller image** is unavailable and the
  analysis says why (as the Shrink dialog does today).
- **Image analysis is slow** (large compressed image): the analysis area shows that it is
  analyzing, and the rest of the window stays usable.
- **Image changed while analysis is running**: the analysis of the old image is discarded.
- **Language switch during an operation**: tooltips and labels update without affecting the
  running operation (as today).

## Requirements *(mandatory)*

### Functional Requirements

**Layout (Backup and Restore tabs)**

- **FR-001**: The Backup and Restore tabs MUST be split into a left area and a right options
  column above a full-width bottom area.
- **FR-002**: The left area MUST hold the device list, the "no devices" hint, the unsuitable-reader
  warning, the "Back up anyway" check box (Backup tab only), and the partition list (when shown).
  The device list MUST take all remaining height of the left area.
- **FR-003**: The right column of the Backup tab MUST hold, in this order: backup scope (whole
  device / single partition), format with fast compression, and the after-backup options (verify,
  eject). The right column of the Restore tab MUST hold the after-writing options (read back and
  verify, eject).
- **FR-004**: The right column MUST show short group headings ("Backup scope", "Format",
  "After backup", "After writing") above its groups of options.
- **FR-005**: The bottom area MUST hold the image path caption, the path box with **Browse…**, on
  the Restore tab the image description, checksum files and size warning, and the action button
  at the bottom right.
- **FR-006**: **Start backup** and **Write to device…**, their enabling rules, and the progress
  panel MUST stay as they are now. **Verify image** and **Shrink image…** MUST be removed from the
  Restore tab.

**Image tab**

- **FR-007**: The window MUST have a third tab, **Image**, after Restore.
- **FR-008**: The Image tab MUST show, on the left, the image path with **Browse…**, the image
  description and checksum files, and the shrink analysis: the partitions, the data used in the
  Linux (ext4) partition, the smallest possible image size, and the reasons when the image cannot
  be shrunk.
- **FR-009**: The right column of the Image tab MUST offer the shrink target ("Smallest possible",
  "Size of a device" with a list of the connected devices and their sizes, "Card size" with a size
  and a unit) and the output format (compressed / uncompressed).
- **FR-010**: The bottom area of the Image tab MUST hold the output path with **Browse…** and the
  **Verify image** and **Create smaller image** buttons at the bottom right.
- **FR-011**: **Create smaller image** MUST produce the same result as the current Shrink dialog
  for the same source, target size, format and output path, including the free-space check, the
  verification of the result and the result message. It MUST never write to any device.
- **FR-012**: **Create smaller image** MUST be available only for a full-device image that can be
  shrunk to the chosen target; otherwise it MUST be unavailable with the reason visible.
- **FR-013**: **Verify image** MUST work for any image and behave as the current button on the
  Restore tab.
- **FR-014**: The output path MUST be pre-filled with a name derived from the source image and the
  chosen target size and format, and MUST follow changes of the target and the format until the
  user edits it.
- **FR-015**: The standalone Shrink image dialog MUST be removed; the Image tab replaces it.

**Shared image and size warning**

- **FR-016**: The Restore and Image tabs MUST share one selected image: selecting or clearing it on
  one tab MUST show the same image, description and checksum files on the other.
- **FR-017**: The selected image MUST be re-read after an operation that saves checksum files, on
  both tabs.
- **FR-018**: On the Restore tab, when the image's required size exceeds the selected target
  device, a warning MUST be shown under the image description: that the image will be shrunk to
  fit before writing, or, when it cannot be shrunk, that it cannot be written. The existing
  "Shrink and write" flow after pressing **Write to device…** MUST stay unchanged.

**Tooltips, localization, window**

- **FR-019**: The labels listed in the Tooltips table MUST be shortened to their visible text, and
  their former parenthesized explanation MUST be shown as a tooltip on that control.
- **FR-020**: Every new or changed text (tab name, short labels, tooltips, group headings, Image
  tab texts, size warning) MUST exist in all six UI languages and MUST follow a language switch
  immediately.
- **FR-021**: The default window size MUST be large enough to show both columns with the device
  list's device, size, bus, volumes and table columns visible, and the window MUST still fit on a
  1024×768 screen.
- **FR-022**: The right column MUST size itself to its content in the current language, wrapping
  long option texts rather than clipping them; the left area MUST get the rest of the width.
- **FR-023**: Apart from the changes listed above, behavior MUST NOT change: the same settings are
  remembered, the same validation and confirmations apply, and the same operation requests are
  produced for the same choices.
- **FR-024**: While an operation runs, all three tabs MUST be disabled for input, as the two tabs
  are today.

### Key Entities

- **Option group**: a heading plus the options under it in a right column (Backup scope, Format,
  After backup, After writing, Shrink to, Output format).
- **Tooltip text**: a localized explanation attached to one control; it replaces the former
  parenthesized part of that control's label.
- **Selected image**: the one image file shared by the Restore and Image tabs, with its
  description, checksum files and (on the Image tab) its shrink analysis.
- **Shrink target**: smallest possible, the size of a chosen connected device, or a typed card
  size.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: At the default window size, the device list on the Backup and Restore tabs shows at
  least 25% more rows than before.
- **SC-002**: In each of the six languages, every label in the right columns is fully readable
  (not clipped) at the default window size and at 100%, 150% and 200% display scaling.
- **SC-003**: Every control in the Tooltips table shows its tooltip in all six languages.
- **SC-004**: All existing automated tests pass, and the resource parity check passes with the new
  texts.
- **SC-005**: A user can complete a backup and a restore with the new layout without any extra
  clicks compared with the old layout.
- **SC-006**: Creating a smaller image sized to a connected device takes no more steps than with
  the old Shrink dialog and needs no typing of a size.
- **SC-007**: An image selected on one of the Restore and Image tabs is shown on the other in 100%
  of cases, including after an operation that saves checksum files.

## Assumptions

- Menus, the confirmation, "Shrink and write" offer, diagnostics and About dialogs, and the
  progress panel are out of scope, apart from the new tab header.
- Parentheses that contain data (sizes, file names, counts such as "Image: {0} ({1})"), file
  dialog filters, and error or refusal messages are not explanations and stay as they are.
- The format lists keep the item names "Compressed" and "Uncompressed"; the file extension is
  still visible in the path box, so moving it into the tooltip loses no information.
- Tooltips appear on hover with the system's usual delay; no help icons are added.
- "Size of a device" lists the same devices as the device lists; their size is only read, so the
  disk the program runs from may also be listed there.
- The shrink analysis starts automatically when a full-device image is selected, as the Shrink
  dialog does today.
- The output format of the Image tab defaults to uncompressed, as in the current Shrink dialog.
- The new layout keeps the current Windows 7 – Windows 11 support and the auto-sizing approach
  used for long translations.
- The default window width grows (to roughly 980 px at 100% scaling) to make room for the right
  column; the height stays as it is.
