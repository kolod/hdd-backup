---

description: "Task list for feature 005: image conversion and checksum calculation"
---

# Tasks: Image Conversion and Checksum Calculation

**Input**: Design documents from `/specs/005-image-conversion/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md

**Tests**: Included for the Core engine work, as required by constitution Principle VI (image I/O
must be test-verified). UI behavior is checked with the quickstart scenarios.

**Organization**: by user story (US1 gzip → raw, US2 raw → gzip, US3 checksums).

## Format: `[ID] [P?] [Story] Description`

- Paths: `src/HddBackup.Core/`, `src/HddBackup.App/`, `tests/HddBackup.Core.Tests/`
- Strings: every new or changed key goes into all six
  `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx` in the same task

---

## Phase 1: Setup

- [ ] T001 Merge PR #9 (`fix/gzip-progress`) if still open, then `git fetch` and rebase `005-image-conversion` onto `origin/main`; confirm `src/HddBackup.Core/Imaging/ImageHead.cs`, `src/HddBackup.Core/Progress/ImageProgress.cs` and `ImageDescriptor.ProgressSize` exist; build and run all tests and record the passing count in `specs/005-image-conversion/quickstart.md` (Results table)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: request/result model, destination checks and the generic conversion operation used
by US1 and US2

- [ ] T002 [P] Extend `src/HddBackup.Core/Imaging/OperationRequest.cs`: add `OperationKind.ConvertImage` (doc: "Copy an image into the other format; the content is unchanged"), `string OutputPath` (destination for `ConvertImage`; format from its extension, `.gz` = gzip) and `bool SaveMissingChecksums` (for `VerifyImage`: after a verification without mismatch, write the sidecars that are missing)
- [ ] T003 [P] Extend `src/HddBackup.Core/Imaging/OperationResult.cs`: `string OutputPath` (committed converted image) and `IReadOnlyList<string> SavedChecksumFiles` (file names written by the operation; default empty)
- [ ] T004 [P] In `src/HddBackup.Core/Imaging/DestinationChecks.cs`, extract the FAT-limit and free-space rules into `public static IReadOnlyList<ValidationWarning> CheckOutputFile(string path, long? size, PathInfo destination)` (folder exists; FAT and size > 4 GB: throw `FileTooLargeForFs` for raw, warn `MayExceedFat32Limit` for gzip or unknown size; raw with known size: throw `DestinationFull` when free < size + 64 KiB) and call it from `Check` so backup behavior is unchanged
- [ ] T005 Implement `ValidateConvert(OperationRequest)` in `src/HddBackup.Core/Imaging/ImagingEngine.cs` and route `Validate` to it for `ConvertImage`: `OutputPath` set (else `InvalidRequest`); `Path.GetFullPath(OutputPath)` ≠ source (else `InvalidRequest`); output format (`ImageDescriptor.IsGzipPath`) ≠ source format (else `InvalidRequest`); then `DestinationChecks.CheckOutputFile(OutputPath, gzip ? null : image.DataSize ?? image.ProgressSize, _access.GetPathInfo(OutputPath))`
- [ ] T006 Implement `RunConvert` in `src/HddBackup.Core/Imaging/ImagingEngine.cs` (route from `Run`; `RunAsync` uses `new PhaseModel(verifyAfter: true)` for `ConvertImage`): load the source `ImageDescriptor`; call `ValidateConvert`; open `ImageReader(source)` and `ImageWriter(OutputPath, gzip, level from Compression)`; phase Transferring with `ImageProgress(reader.FileLength, image.ProgressSize)` units; pipeline reads with `ReadFull`, appends to a `Sha256Accumulator`, writes with `writer.Write(buffer, 0, n, offset)`; then `result.DataBytes`, `ContentSha256`, `FileSha256 = reader.FinishFileHash()` of the source for comparison, `result.Verifications = CompareChecksums(image, result)` and throw `ChecksumMismatch` on any mismatch; Flushing: `writer.Complete()`; set `result.FileSha256` to the output file hash (`gzip ? writer.FileHash : ContentSha256`) and `result.IsGzipImage = gzip`
- [ ] T007 Continue `RunConvert`: write output sidecars as partials per research R-3 — file sidecar always for gzip output; content sidecar (`ImageDescriptor.ContentSidecarPath` for gzip output, `FileSidecarPath` for raw output) only when it does not already exist with the same hash and label (read it with `ChecksumFile.Read`, compare `Hash`); copy `PartitionMetadataFile.PathFor(source)` to `PartitionMetadataFile.PathFor(OutputPath) + ".partial"` when the source is a partition image; Verifying phase: `VerifyWrittenImage(writer.PartialPath, result, model, new ImageProgress(writer.FileLength, result.DataBytes), ct)`; commit with `CommitFiles(partials)` (image last); fill `result.OutputPath` and `result.SavedChecksumFiles` (names of committed sidecars); on failure or cancellation `writer.Abort()` and delete all partials (same `finally` pattern as `RunBackup`); `SafeToRemove = true`, no device access

**Checkpoint**: `ConvertImage` works in both directions from Core.

---

## Phase 3: User Story 1 - Convert a compressed image to an uncompressed one (Priority: P1) 🎯 MVP

**Goal**: `.img.gz` → `.img` from the Image tab, with checksum and verification.

**Independent Test**: convert a compressed fixture and compare the result with the known raw image
and its checksum.

- [ ] T008 [P] [US1] Create `tests/HddBackup.Core.Tests/Imaging/ConvertImageTests.cs` (copy fixtures to a temp folder per test): `GzipToRaw_EqualsRawFixture` for `rpi-like-4k` and `rpi-like-gpt` (bytes equal `FixtureImages.RawPath`, `disk.img.sha256` matches, source and its sidecars unchanged by hash and timestamp); `GzipToRaw_SourceContentMismatch_FailsAndLeavesNothing` (write a wrong content sidecar next to the gzip copy); `GzipToRaw_Cancelled_LeavesNothing` (cancel after the first progress); `PartitionImage_CopiesMetadata` (make a partition image with the engine's `BackupPartition` on a `MemoryBlockDevice`, convert, check `.partition.json` next to the output equals the source's); `Validate_RejectsSamePathAndSameFormat`; `RawOutput_NotEnoughSpace_Refused` (fake `IDeviceAccess.GetPathInfo` with small `FreeBytes`)
- [ ] T009 [US1] Run the tests from T008 and fix `RunConvert` until they pass
- [ ] T010 [P] [US1] Add strings to all six `.resx`: `Image_SameSize` "Same as the image", `Group_NewImageSize` "Size of the new image", `Image_Output` "Save the new image as:", `Image_SameAsSource` "The new image would be the same as the source. Choose another format or size.", `Phase_Convert_Transferring` "Converting the image…", `Result_Converted` "Converted image saved: {0}", `Result_ChecksumsSaved` "Checksum files saved: {0}"; change `Image_Create` to "Create image"
- [ ] T011 [US1] In `src/HddBackup.App/Forms/ImageTab.cs`: add the radio `_same` (`Image_SameSize`) first in the size group, checked by default; caption `Group_NewImageSize`; output caption `Image_Output`; with `_same` checked enable size/format/output controls for any loaded image (not only shrinkable ones), default the format to the opposite of the source format when the image changes, and suggest `ImageFileName.WithExtension(source, IsGzip)` as output; Create enabled when output ≠ source and format ≠ source format, otherwise show `Image_SameAsSource` in the reason label; Create raises `StartRequested` with `Kind = ConvertImage`, `ImagePath`, `OutputPath`, `Compression`, `EjectAfter = false`; other size choices keep the `ShrinkImage` request
- [ ] T012 [US1] In `src/HddBackup.App/Forms/OperationPanel.cs`: phase label `Phase_Convert_Transferring` for `ConvertImage` (Flushing and Verifying reuse the generic labels); result lines for `ConvertImage`: `Result_Converted` with `OutputPath`, and `Result_ChecksumsSaved` with `SavedChecksumFiles` when not empty; title/state texts as for verify (no device, safe to remove)
- [ ] T013 [US1] In `src/HddBackup.App/Forms/MainForm.cs`: `StartOperation` validates `ConvertImage` like other kinds (show warnings `MayExceedFat32Limit` with the backup question); after a `ConvertImage` completes, reload the shared image (`_image.Reload()`) so sidecars shared with the source show up
- [ ] T014 [US1] Build; run quickstart scenarios 1, 2, 4, 5 and record results

**Checkpoint**: MVP — `.img.gz` → `.img` from the UI.

---

## Phase 4: User Story 2 - Convert an uncompressed image to a compressed one (Priority: P1)

**Goal**: `.img` → `.img.gz` with both sidecars and the fast compression choice.

**Independent Test**: compress a raw fixture; decompressing the result gives the original bytes;
both checksum files match.

- [ ] T015 [P] [US2] Add to `tests/HddBackup.Core.Tests/Imaging/ConvertImageTests.cs`: `RawToGzip_RoundTrip` (decompress the output with `GZipStream` and compare to the source bytes; `.img.gz.sha256` = SHA-256 of the output file; content sidecar = source content hash), `RawToGzip_FastestCompression_RoundTrip`, `RawToGzip_ExistingContentSidecarWithSameHash_IsNotRewritten` (source `disk.img` has `disk.img.sha256`; after converting to `disk.img.gz` in the same folder that file is byte-identical and its timestamp unchanged), `RawToGzip_ToFat_WarnsMayExceed` (fake FAT path info)
- [ ] T016 [US2] Run the tests from T015 and fix `RunConvert` until they pass
- [ ] T017 [US2] In `src/HddBackup.App/Forms/ImageTab.cs`: add check box `_fast` (`Option_FastCompression`, tooltip `Tooltip_FastCompression`) under the format list; value from and saved to `PortableSettings.FastCompression`; enabled only when the format is Compressed; pass `Compression` (`Fastest` / `Optimal`) in both `ConvertImage` and `ShrinkImage` requests
- [ ] T018 [US2] Build; run quickstart scenario 3 and the `gzip -dc | cmp` check; record results

---

## Phase 5: User Story 3 - Calculate and save checksums (Priority: P2)

**Goal**: one action that calculates and saves missing checksum files; no question afterwards.

**Independent Test**: delete a fixture's sidecars, calculate them, compare with `sha256sum`.

- [ ] T019 [P] [US3] Create `tests/HddBackup.Core.Tests/Imaging/ChecksumCalculationTests.cs`: raw image without sidecars + `SaveMissingChecksums` → `disk.img.sha256` with the correct hash and `SavedChecksumFiles` lists it; gzip without sidecars → both files; gzip with only `.img.gz.sha256` → `.img.sha256` added, existing file untouched; gzip with a wrong `.img.gz.sha256` → `ChecksumMismatch`, nothing saved; cancelled → nothing saved; `SaveMissingChecksums = false` → nothing saved (old behavior)
- [ ] T020 [US3] In `src/HddBackup.Core/Imaging/ImagingEngine.cs` `RunVerify`: after `CompareChecksums` without mismatch and when `request.SaveMissingChecksums`, write only the missing sidecars (file sidecar if `image.FileChecksum == null`; for gzip, content sidecar if `image.ContentChecksum == null`) as partials and commit them; set `result.SavedChecksumFiles`; make `SaveChecksumFiles` reuse the same helper; run the tests from T019
- [ ] T021 [P] [US3] Add string `Image_CalculateChecksums` "Calculate checksums" to all six `.resx`
- [ ] T022 [US3] In `src/HddBackup.App/Forms/ImageTab.cs`: the verify button reads `Image_CalculateChecksums` when the loaded image has no checksum file (`!Descriptor.HasAnyChecksum`), otherwise `Image_Verify`; it always sends `VerifyImage` with `SaveMissingChecksums = true`; update the label in `ApplyStrings` and on image change
- [ ] T023 [US3] In `src/HddBackup.App/Forms/MainForm.cs`: remove `OfferToSaveChecksums` and its call; after `VerifyImage` completes, call `_image.Reload()` when `SavedChecksumFiles` is not empty; in `src/HddBackup.App/Forms/OperationPanel.cs` show `Result_ChecksumsSaved` for `VerifyImage` results with saved files
- [ ] T024 [US3] Remove `Verify_SaveChecksumsQuestion` and `Verify_ChecksumsSaved` (and `Group_ShrinkTo`) from all six `.resx` after confirming no references in `src/`; build; run quickstart scenarios 6 and 7

---

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T025 Run all tests; check that every key used in `src/HddBackup.App` exists in all six `.resx` and no static key is unused (same check as feature 004)
- [ ] T026 Screenshots of the Image tab in all six languages at the default size and at 800 × 560 (Same as the image selected, compressed and uncompressed source) — no clipped text; record in quickstart
- [ ] T027 [P] Update `README.md` (Image tab: conversion and checksum calculation), `docs/release-notes.md` (Unreleased section) and `docs/manual-hardware-checklist.md` (one conversion step and one checksum step on real files)
- [ ] T028 Run quickstart scenarios 8–10 (corrupt source, cancel, free space) and record the results in `specs/005-image-conversion/quickstart.md`

---

## Dependencies & Execution Order

- Phase 1 (rebase onto PR #9) → Phase 2 → stories.
- US1 and US2 both depend on Phase 2; they share `ImageTab.cs` and `ConvertImageTests.cs`, so do
  US1 then US2.
- US3 depends only on Phase 2 (T002, T003) for the engine part (T019, T020) and can run in
  parallel with US1/US2 in Core; its UI tasks (T022, T023) follow US1 because of `ImageTab.cs` /
  `MainForm.cs`.
- Resource tasks (T010, T021, T024) edit the same six files: run them one after another.

### Parallel Opportunities

- Phase 2: T002, T003, T004 in parallel; then T005 → T006 → T007.
- T008 (tests) in parallel with T010 (strings).
- T019/T020 (checksum engine) in parallel with US1 UI tasks.
- T027 docs in parallel with T026.

## Parallel Example: Phase 2

```text
Task: "T002 Extend OperationRequest.cs"
Task: "T003 Extend OperationResult.cs"
Task: "T004 CheckOutputFile in DestinationChecks.cs"
```

## Implementation Strategy

1. Phase 1–2: rebase, engine operation.
2. US1 (MVP): `.img.gz` → `.img` end to end; validate scenarios 1, 2, 4, 5.
3. US2: compression direction and fast compression.
4. US3: checksum calculation, question removed.
5. Polish: strings, screenshots, docs, failure scenarios.
