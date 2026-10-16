# UI Contract: Image Tab Additions

Extends [specs/004-two-column-layout/contracts/ui.md](../../004-two-column-layout/contracts/ui.md)
(Image tab). Approved mockup: [spec.md](../spec.md#layout-mockup-approved).

## Right column

| Group | Controls |
|-------|----------|
| **Size of the new image** (was "Shrink to") | (•) Same as the image · ( ) Smallest possible · ( ) Size of a device + list · ( ) Card size + number + unit |
| **Output format** | Format list (Compressed / Uncompressed, tooltip per item) · Fast compression `*` (enabled for Compressed) |

## Bottom area

"Save the new image as:" (was "Save the smaller image as:"), output path + **Browse…**,
buttons **Calculate checksums** / **Verify image** (one button, label by state) and **Create image**.

## Behavior

| Action | Request | After completion |
|--------|---------|------------------|
| Create image, Same as the image | `ConvertImage` (`ImagePath`, `OutputPath`, `Compression`) | Result panel: "Converted image saved: <path>" and saved checksum files |
| Create image, other sizes | `ShrinkImage` as before, plus `Compression` | As before |
| Calculate checksums / Verify image | `VerifyImage` with `SaveMissingChecksums = true` | Result panel lists verified and saved checksums; shared image reloaded (both tabs show new sidecars) |

- Overwrite question before **Create image** when the output exists (text `Backup_ConfirmOverwrite`).
- The verify-then-ask question ("save the computed checksums?") is removed.
- While an operation runs all tabs are disabled (unchanged).

## Strings (six languages)

| Change | Keys |
|--------|------|
| New | `Image_SameSize` "Same as the image", `Group_NewImageSize` "Size of the new image", `Image_CalculateChecksums` "Calculate checksums", `Image_SameAsSource` "The new image would be the same as the source. Choose another format or size.", `Image_Output` "Save the new image as:", `Phase_Convert_Transferring` "Converting the image…", `Result_Converted` "Converted image saved: {0}", `Result_ChecksumsSaved` "Checksum files saved: {0}" |
| Changed | `Image_Create` "Create image" |
| Removed if unreferenced | `Group_ShrinkTo`, `Verify_SaveChecksumsQuestion`, `Verify_ChecksumsSaved` |
