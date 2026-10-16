# Research: Image Conversion and Checksum Calculation

## R-1. Engine operation for conversion

- **Decision**: a new `OperationKind.ConvertImage` handled by `ImagingEngine.RunConvert`. It reads
  the source with `ImageReader` (raw or gzip), hashes the content (`Sha256Accumulator`), writes the
  output with `ImageWriter` (gzip level from `CompressionChoice`), then follows the backup path:
  checksum sidecars and a partition-metadata copy as `.partial` files, read-back verification of
  the written file (`VerifyWrittenImage`), and `CommitFiles` with the image renamed last. On any
  failure or cancellation the `.partial` files are deleted.
- **Rationale**: every building block exists and is tested for backups; the only new part is the
  source (an image file instead of a device). Same failure semantics (no partial result, FR-009,
  SC-005).
- **Alternatives considered**: implementing conversion as a "shrink with no target": the shrink
  pipeline rebuilds ext4 and only accepts whole-device images; conversion must work for any image.
  Copying the file and compressing it with `GZipStream` in the UI layer: no progress, no
  cancellation, no verification.

## R-2. Checking the source while converting

- **Decision**: the source's stored checksums are compared at the end of the read pass with the
  existing `CompareChecksums(image, result)` (file hash from `ImageReader.FinishFileHash`, content
  hash from the accumulator). A mismatch fails the operation with `ChecksumMismatch` before
  anything is committed. Missing checksums are not an error (the result simply has no reference).
- **Rationale**: FR-003; reuses the restore/verify rule, so a corrupt source never produces a
  "good-looking" converted image with fresh checksums.

## R-3. Sidecars shared by source and output

- **Decision**: a gzip image and the uncompressed image of the same name share `disk.img.sha256`
  (content checksum of `disk.img.gz` = file checksum of `disk.img`). When the output's sidecar
  path already exists and holds the same hash, it is left untouched; when it holds a different
  hash, it belongs to another file and is replaced together with the output after the overwrite
  confirmation (as for backups). The source's own sidecars are never written (FR-009).
- **Rationale**: converting `disk.img.gz` → `disk.img` next to the original would otherwise
  rewrite the source's content sidecar with an identical value; leaving it alone keeps "the
  source is not changed" literally true.
- **Note**: `ValidationWarning.ContentChecksumCollides` from `DestinationChecks` is not raised for
  conversion when the colliding sidecar has the same hash.

## R-4. Destination checks

- **Decision**: `ValidateConvert` checks: output path set, folder exists, output ≠ source (full
  path, case-insensitive), not (same format as source) — FR-008; FAT destination: refuse an
  uncompressed output over 4 GB, warn `MayExceedFat32Limit` for compressed; free space: for an
  uncompressed output require `size + 64 KiB` where size = `ImageDescriptor.DataSize ??
  ProgressSize` (estimate from feature fix/gzip-progress); otherwise the write fails with
  `DestinationFull` and no partial file. Reuses the FAT and free-space rules of
  `DestinationChecks` through a small shared helper taking a size instead of a device.
- **Dependency**: `ImageDescriptor.ProgressSize`, `ImageHead` and `ImageProgress` from PR #9
  (fix/gzip-progress) — needed for the size estimate and for honest progress when the source is
  `.img.gz`. This feature is implemented after PR #9 is merged.

## R-5. Progress phases

- **Decision**: `PhaseModel(verifyAfter: true)` with Transferring (units from `ImageProgress` over
  the source), Flushing, Verifying (read-back of the output) and no Releasing/Ejecting. Phase
  labels: new `Phase_Convert_Transferring` ("Converting the image…"); Flushing and Verifying reuse
  the backup labels.

## R-6. Calculating and completing checksums

- **Decision**: `OperationRequest.SaveMissingChecksums` (bool) on `OperationKind.VerifyImage`.
  After a verification pass without mismatch, the engine writes only the sidecars that are
  missing (file and/or content), via a variant of `SaveChecksumFiles` that skips existing ones,
  and lists them in `OperationResult.SavedChecksumFiles`. With a mismatch nothing is saved
  (FR-011). The UI sets the flag always; `MainForm.OfferToSaveChecksums` and its question
  (`Verify_SaveChecksumsQuestion`) are removed. The button label is **Calculate checksums** when
  the image has no checksum file at all, otherwise **Verify image**.
- **Rationale**: one pass, cancellable, inside the operation (no modal question afterwards);
  the existing verify path already computes both hashes.
- **Alternatives considered**: a separate `OperationKind.ComputeChecksums`: identical reading
  code; the only difference is whether stored values are compared, which the verify path already
  handles (no stored value = not available).

## R-7. Image tab changes

- **Decision**: add the radio **Same as the image** at the top of the size group (default); rename
  the group caption (`Group_NewImageSize`) and the button (`Image_Create` → "Create image"); add
  **Fast compression** under the format list, bound to `PortableSettings.FastCompression` and
  enabled only for the compressed format. With **Same as the image**: the request is
  `ConvertImage`; available for any loaded image; default output name
  `ImageFileName.WithExtension(source, gzip)`; the shrink analysis stays visible as information.
  The default output format with **Same as the image** is the opposite of the source format, so
  the suggested action is a conversion. Other size choices keep the shrink behavior, and now also
  pass the compression choice.
- **Strings**: new `Image_SameSize`, `Group_NewImageSize`, `Image_CalculateChecksums`,
  `Image_SameAsSource` (reason), `Phase_Convert_Transferring`, `Result_Converted` ({0} path),
  `Result_ChecksumsSaved` ({0} file names); changed `Image_Create`; removed `Group_ShrinkTo`,
  `Verify_SaveChecksumsQuestion`, `Verify_ChecksumsSaved` if unused.
