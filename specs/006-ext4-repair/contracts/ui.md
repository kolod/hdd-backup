# UI Contract: ext4 Repair

Extends the Image tab of features 004/005. Approved mockup: [spec.md](../spec.md#layout-mockup-approved).

## Image tab

| Area | Addition |
|------|----------|
| Left | **File system** heading above "Shrink analysis"; one line per ext4 partition: "#2 rootfs (ext4): clean" / "needs repair — <reasons>" / in red "cannot be repaired by this program — <problems>. Repair it with a Linux file system check and back up again." |
| Right | **File system** heading + check box **Repair the file system** `*` (tooltip `Tooltip_Repair`) after the output format group; shown only when the root partition needs repair, checked by default |

| State | Same as the image | Shrink sizes |
|-------|-------------------|--------------|
| Root clean | as feature 005 (conversion) | as before |
| Root needs repair, Repair checked | `ShrinkImage` at the current size with `RepairFileSystem`; any output format (source format allowed); default name `<name>_repaired.img[.gz]` | `ShrinkImage` with `RepairFileSystem`; name `<name>_repaired_shrunk…` |
| Root needs repair, Repair unchecked | conversion only (feature 005), the file system is copied as is | unavailable; reason `Image_RepairRequired` |
| Root damaged | conversion only | unavailable; reason = the damage line |

## Restore tab

- `ImageFit`: `NeedsRepair` counts as shrinkable; the size warning reads `Restore_TooLargeRepair`
  ("… It will be repaired and shrunk to fit before writing.").
- The "Shrink and write" offer adds the line `Shrink_OfferRepair` and the shrink step runs with
  `RepairFileSystem = true`.

## Operation panel

- Preparing shows `Phase_Repair_Journal` ("Replaying the journal…") while the overlay is built.
- Result: `Result_Repaired` ("Repaired image saved: {0}") for same-size repairs; shrink results as
  before plus `Result_RepairDone` ("The file system was repaired: {0}") listing what was done
  (journal transactions applied, orphan inodes removed).

## Strings (six languages)

`Image_FileSystemHeading`, `Image_FsClean` ({0} partition), `Image_FsNeedsRepair` ({0} partition,
{1} reasons), `Image_FsDamaged` ({0} partition, {1} problems), `Reason_Journal`,
`Reason_Orphans` ({0} count), `Reason_NotClean`, `Group_FileSystem`, `Image_Repair`,
`Tooltip_Repair`, `Image_RepairRequired`, `Restore_TooLargeRepair`, `Shrink_OfferRepair`,
`Phase_Repair_Journal`, `Result_Repaired`, `Result_RepairDone`.
