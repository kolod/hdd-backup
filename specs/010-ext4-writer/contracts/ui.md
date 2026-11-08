# UI Contract: Edit Mode on the Files Tab

Draft mockups: [spec.md](../spec.md#layout-mockup-draft-to-be-approved). Extends the Files tab of
features 008 and 009; outside edit mode nothing changes.

## Buttons (bottom row, right to left: Extract…, Tools, Save, Discard, Edit)

| Control | Behaviour |
|---------|-----------|
| **Edit** (toggle, left of Tools) | off → on: image: checks (R-2), reads or creates the diff file; card: checks, then the confirmation `EditCardDialog`. on → off: with waiting changes asks (Keep in the diff file / Save / Discard / Cancel) |
| **Save** | image: enabled when changes wait → `SaveChangesDialog` → engine operation `SaveEdits`. card: enabled when the open file's text is changed → writes that file at once |
| **Discard** | images only (hidden on a card); enabled when changes wait; asks; removes diff file |
| Enabled when | a file system is open and no extraction, tool or save runs; refused sources keep **Edit** disabled with the reason as tooltip |

## Viewer and tree in edit mode

- The viewer text box is writable for editable text files (R-12); otherwise read-only with the reason
  in the note line. The header shows `EDITING` (card: `EDITING THE CARD`).
- The tree context menu: **New file…**, **New folder…**, **Rename…** (F2), **Delete** (Del). Names are
  asked inline; invalid names show the reason and are not accepted.
- Pending entries (images): new = bold with a plus glyph, edited = bold, renamed = italic, deleted =
  struck through and grey; the status line shows `{0} changes not saved`; a card shows
  `Editing the card: changes are written immediately`.
- Changing selection with an unsaved text edit: image → the edit becomes a pending change; card → asks
  (Save to the card / Discard the edit / Cancel).

## Dialogs

| Dialog | Content |
|--------|---------|
| `EditCardDialog` | model, size, bus, partition; "Every change you make is written to the card at once and cannot be undone. Only the ext4 partition is written…"; **Edit the card** / **Cancel** (default) |
| `SaveChangesDialog` | list of changes with glyphs and sizes; the checksum file that is updated or created; "The image is changed in place…"; **Save** / **Cancel** (default) |
| Delete confirmation | "Delete {0}? It contains {1} files and directories ({2})." (card: always; image: only for directories) |
| Interrupted save | on opening an image with a redo log: "A save was interrupted…" **Finish save** / **Not now** |
| Image changed | "The waiting changes belong to a different version of this image." **Discard** only |

## Operation panel (image Save)

Phases `Edit_Phase_Prepare`, `Edit_Phase_Write`, `Edit_Phase_Checksum`; **Cancel** works at any time
(banner: "The save was interrupted; open the image to finish it" or, in the checksum phase, "The image
was changed; its checksum is stale"). Results: `Edit_Result_Saved`, `Edit_Result_SavedStale`,
`Edit_Result_Interrupted`, `Edit_Result_Failed`.

## Strings (English; all six languages required)

| Group | Keys |
|-------|------|
| Buttons | `Edit_Button` "Edit", `Edit_Save` "Save", `Edit_Discard` "Discard", tooltips `Tooltip_Edit*` |
| Status | `Edit_Editing`, `Edit_EditingCard`, `Edit_ChangesNotSaved` "{0} changes not saved", `Edit_SavedFile` |
| Menu | `Edit_NewFile`, `Edit_NewFolder`, `Edit_Rename`, `Edit_Delete` |
| Dialogs | `EditCard_Title/Question/Warning/Confirm`, `SaveChanges_Title/Intro/Checksum/InPlace`, `Edit_DeleteQuestion`, `Edit_Interrupted*`, `Edit_ImageChanged*`, `Edit_Keep/Save/Discard/Cancel` |
| Change kinds | `Edit_Kind_{EditContent,CreateFile,CreateDirectory,Delete,Rename}` with `{0}` path, `{1}` detail |
| Refusals | one string per code of the data model (`EditRefused_*`, `ChangeRefused_*`, `SaveRefused_*`) |
| Operation | `Edit_Phase_*`, `Edit_Result_*`, `Edit_ChecksumStale` |

A resource-parity test requires a string for every enum value of the reason and kind codes. Texts
stay short; long explanations are in tooltips (checked with the scripted harness at 100%).

## Behaviour notes

- Browse mode (Edit off) is byte-for-byte the feature 008/009 behaviour; the tests of those features
  must still pass unchanged.
- Extract and the Tools use the on-disk view and show the note `Edit_NotIncluded` ("Waiting changes
  are not included.") while changes wait.
- A language switch re-renders the buttons, status and open dialogs' static texts; waiting changes are
  kept as data.
