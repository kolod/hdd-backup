# UI Contract: Files Tab

Draft mockup: [spec.md](../spec.md#layout-mockup-draft-to-be-approved). The menu and the operation
panel are unchanged. Tab order: Back up, Restore, Image, **Files**.

## Controls

| Area | Control | Behavior |
|------|---------|----------|
| Source | Radio **Image file** / **Device** | switches which of the two rows below is enabled; changing it closes the open file system |
| Source | Image path box + **Browse…** | shared with the Restore and Image tabs (`SelectedImage`); filter `*.img`; a `.img.gz` shows `Files_NeedsConversion` and opens nothing |
| Source | Device drop-down | same devices as the Back up tab, refreshed on device change; read-only, so no write confirmation |
| Partition | Drop-down + state text | ext4 partitions as `#2 rootfs (ext4) 28.9 GiB`; a single choice is selected automatically; the state text is `Files_StateClean`, or a warning (below) |
| Tree | `TreeView` | root `/`; directories expand on demand; icons: folder, file, link, special, unreadable; sorted directories first |
| Viewer | Header label | path, type, size, mode, uid/gid, time; for a link its target; for an unreadable item its reason |
| Viewer | Read-only monospaced text box | file beginning as text or hex; empty for non-files |
| Viewer | Radio **Text** / **Auto** / **Hex** `*` | default Auto; changing it re-renders the same bytes |
| Bottom | Status line | counts (`Files_Counts`), or the open/refresh progress, or the error |
| Bottom | **Extract…** `*` | enabled for a selected, readable entry when no operation runs; also in the tree's context menu (**Extract…**) |

## States

| State | Tree / viewer | Partition state text | Extract |
|-------|---------------|----------------------|---------|
| No source | empty | empty | disabled |
| `.img.gz` selected | empty | `Files_NeedsConversion` | disabled |
| No ext4 partition | empty | `Files_NoExt4` + partitions present | disabled |
| Clean | shown | `Files_StateClean` | enabled |
| Needs repair | shown | warning `Files_StateNeedsRepair` ("…recent changes may be missing or inconsistent; repair the image on the Image tab first") | enabled |
| Damaged (errors recorded) | shown | warning `Files_StateDamaged` | enabled |
| Not browsable partition | empty | reason (`Files_Unsupported` + feature) | disabled |
| Source read error | cleared | error text with the cause | disabled |
| Operation running | tree and viewer frozen, source and partition disabled | unchanged | disabled (cancel is on the panel) |

## Extract flow

1. **Extract…** → folder browser dialog (destination).
2. The tab runs the planner on a worker thread (status `Files_Planning`): total files, directories,
   bytes, conflicts, per-item problems.
3. Refused before anything is written, with a message: no free space (`Extract_NoSpace`, both
   sizes), destination is the image or on the browsed device (`Extract_DestinationIsSource`),
   nothing to extract.
4. Conflicts → `ExtractConflictDialog`: "{0} files already exist in the destination" with
   **Replace**, **Skip existing**, **Cancel**.
5. The engine runs `ExtractFiles`; the operation panel shows phases `Phase_Planning` and
   `Phase_Extracting`, bytes, speed, elapsed, remaining, and **Cancel**.
6. Result: panel banner `Result_Extracted` ("{0} files and {1} directories extracted to {2}") and,
   when issues exist, `Result_ExtractedWithIssues` with **Details…** →
   `ExtractSummaryDialog` (list: kind, path, resulting name, reason; **Copy** puts the list on the
   clipboard). Cancelled: `Result_ExtractCancelled`. Failed: the standard error banner with cause.

## Strings (English; all six languages required)

| Key | English |
|-----|---------|
| `Tab_Files` | Files |
| `Files_Source` | Source |
| `Files_SourceImage` | Image file |
| `Files_SourceDevice` | Device |
| `Files_Partition` | Partition |
| `Files_StateClean` | Clean |
| `Files_StateNeedsRepair` | Not cleanly unmounted: recent changes may be missing or inconsistent. Repair the image on the Image tab first. |
| `Files_StateDamaged` | The kernel recorded file system errors; some files may be unreadable. |
| `Files_NeedsConversion` | Compressed images cannot be browsed. Convert the image to .img on the Image tab first. |
| `Files_NoExt4` | No ext4 partition was found. Partitions present: {0} |
| `Files_Unsupported` | This partition cannot be browsed: {0} |
| `Files_Counts` | {0} files, {1} directories |
| `Files_CountsApprox` | About {0} files, {1} directories |
| `Files_Opening` | Opening… |
| `Files_Planning` | Preparing extraction… |
| `Files_ReadError` | Could not read the source: {0} |
| `Files_Unreadable` | Unreadable: {0} |
| `Files_NotViewable` | Cannot be shown: {0} |
| `Files_LinkTarget` | Link to {0} |
| `Files_SpecialFile` | Special file ({0}) |
| `Files_ShownOf` | Showing the first {0} of {1} |
| `Files_ViewText` | Text |
| `Files_ViewAuto` | Auto |
| `Files_ViewHex` | Hex |
| `Files_Extract` | Extract… |
| `Tooltip_Files_Extract` | Copies the selected file or directory, with everything in it, to a folder on this computer. |
| `Tooltip_Files_ViewHex` | Shows the bytes with offsets, for binary files. |
| `Tooltip_Files_State` | Whether the file system was unmounted cleanly. The contents are shown as they are on disk; nothing is repaired or changed. |
| `Files_ExtractTitle` | Extract to |
| `Extract_NoSpace` | The destination has {1} free, but {0} are needed. |
| `Extract_DestinationIsSource` | The destination cannot be the image or a volume of the device being browsed. |
| `Extract_Conflicts` | {0} files already exist in the destination. |
| `Extract_Replace` | Replace |
| `Extract_SkipExisting` | Skip existing |
| `Phase_Planning` | Preparing… |
| `Phase_Extracting` | Extracting… |
| `Result_Extracted` | {0} files and {1} directories extracted to {2}. |
| `Result_ExtractedWithIssues` | {0} files and {1} directories extracted to {2}. {3} items were skipped, renamed or failed. |
| `Result_ExtractCancelled` | Extraction cancelled. Files already extracted were kept. |
| `Extract_Details` | Details… |
| `Extract_IssueSkippedLink` | Symbolic link, not created |
| `Extract_IssueSkippedSpecial` | Special file, not created |
| `Extract_IssueSkippedExisting` | Already exists, skipped |
| `Extract_IssueRenamed` | Renamed because Windows cannot store the name |
| `Extract_IssueTooLong` | The path is too long for Windows |
| `Extract_IssueFatLimit` | The file is 4 GiB or larger and the destination is FAT32 |
| `Extract_IssueUnreadable` | Could not be read: {0} |

Tooltips carry the explanations; labels stay short so that German, French and Japanese do not
clip (checked with the layout screenshots at 100% as in features 004–007). `Files_*` strings are
added to all six `Strings*.resx` files; a resource-parity test (existing) fails if one is missing.

## Behavior notes

- The tab never shows a write confirmation dialog; the existing `ConfirmWriteDialog` is not used.
- Selection changes cancel the previous preview read; only the latest selection renders.
- While the partition is being opened the tree shows a single "Opening…" node; a failure replaces
  it with the error text and the tree stays empty.
- Closing the program or changing the source disposes the view and closes the source handles.
