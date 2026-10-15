---

description: "Task list for Shrink ext4 Image to Fit a Smaller Card"
---

# Tasks: Shrink ext4 Image to Fit a Smaller Card

**Input**: Design documents from `/specs/002-ext4-image-shrink/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: INCLUDED. Constitution Principle VI requires them. Tests are written before the
code they cover. Fixtures come from real `mke2fs` (Docker, developer machine only).

**Organization**: Grouped by user story. Story priorities from spec.md: US1 (P1) write to a
smaller card; US3 (P1) refuse what cannot be shrunk safely; US2 (P2) standalone shrink. The
shared ext4 engine is foundational, because every story depends on it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: US1, US2, US3 from spec.md
- All code, comments and docs in English (Principle V)

## Path Conventions

- Core: `src/HddBackup.Core/` (Ext4/, Shrinking/), App: `src/HddBackup.App/`
- Tests: `tests/HddBackup.Core.Tests/` (Ext4/, Shrinking/, Fixtures/ext4/)
- Dev tooling: `tests/fixtures/ext4/`

---

## Phase 1: Setup

- [X] T001 Amend `.specify/memory/constitution.md` to v1.1.0 (MINOR): add "shrinking the last ext4 partition of an image (rebuild, original never modified)" to Principle VII scope and the Technology section; update the Sync Impact Report; reference feature 002
- [X] T002 [P] Create `tests/fixtures/ext4/make-fixtures.sh`. It runs in `debian:bookworm` with e2fsprogs, dosfstools, attr, acl and libcap2-bin, and builds the fixtures listed in quickstart.md: loop-free, using `mke2fs -d` from a staged directory plus `debugfs` for special files. Raspberry Pi OS feature flags: `-O 64bit,metadata_csum,flex_bg,extent,huge_file,dir_nlink,extra_isize,dir_index,resize_inode,has_journal,ext_attr,sparse_super,large_file,filetype`. It writes `*.img.gz` and `*.manifest.txt` (path, type, mode, uid, gid, size, mtime, SHA-256, link target, xattrs) to `tests/HddBackup.Core.Tests/Fixtures/ext4/`
- [X] T003 [P] Create `tests/fixtures/ext4/check-output.sh`. Given (output image, source image), it extracts the ext4 partition of each, runs `e2fsck -fn` on the output (must exit 0 with no output), runs `debugfs -R "rdump / <dir>"` on both, then compares them with `diff -r`, a `stat`-based metadata listing, and `getfattr -R -d -m -`. Exit code non-zero on any difference
- [X] T004 Run `make-fixtures.sh` via Docker (command in quickstart.md), commit the generated fixtures, and add them as `None` items copied to output in `tests/HddBackup.Core.Tests/HddBackup.Core.Tests.csproj`
- [X] T005 [P] Add `tests/HddBackup.Core.Tests/Fixtures/FixtureImages.cs`. It decompresses a named fixture to a temp file once per test run and offers `Open(name)` → `FileBlockDevice` plus a `Manifest(name)` parser

---

## Phase 2: Foundational (ext4 engine, blocks all stories)

**⚠️ CRITICAL**: no user story work before this phase passes its tests.

### Tests first

- [X] T006 [P] Crc32c tests (RFC 3720 vectors, incremental append) in `tests/HddBackup.Core.Tests/Ext4/Crc32cTests.cs`
- [X] T007 [P] FileBlockDevice tests (read-only refuses writes, size, sector alignment) in `tests/HddBackup.Core.Tests/Devices/FileBlockDeviceTests.cs`
- [X] T008 [P] Ext4Reader tests against fixtures. Superblock fields and features; every manifest entry is found with the same type, mode, uid, gid, size, mtime, SHA-256, link target and xattrs; metadata checksums verify. Corrupting one inode byte gives a checksum error. In `tests/HddBackup.Core.Tests/Ext4/Ext4ReaderTests.cs`
- [X] T009 [P] Ext4Layout tests: for sizes 64 MiB, 448 MiB, 4 GiB, 16 GiB and block sizes 1 K / 4 K, group count, backup superblock groups (0, 1, 3^n, 5^n, 7^n), flex_bg placement, reserved GDT block count and inodes per group match the reference values recorded by `make-fixtures.sh` (from `mke2fs -n` + `dumpe2fs -h`). In `tests/HddBackup.Core.Tests/Ext4/Ext4LayoutTests.cs`
- [X] T010 [P] Ext4Writer round-trip tests. Rebuild each positive fixture's ext4 into a new device of minimum size and of a fixed size; `Ext4Checker` reports nothing; `Ext4TreeComparer` reports nothing; reading the result with `Ext4Reader` reproduces the manifest. In `tests/HddBackup.Core.Tests/Ext4/Ext4WriterTests.cs`
- [X] T011 [P] Ext4Checker negative tests: a flipped bitmap bit, a wrong free count, a bad group checksum, a bad inode checksum, a dangling dirent, a wrong link count, and an extent pointing outside the FS are each reported. In `tests/HddBackup.Core.Tests/Ext4/Ext4CheckerTests.cs`
- [X] T012 [P] Ext4TreeComparer tests: changed content, mode, xattr, symlink target, and a missing file are each reported. In `tests/HddBackup.Core.Tests/Ext4/Ext4TreeComparerTests.cs`

### Implementation

- [X] T013 [P] Implement `Crc32c` (Castagnoli, table-driven, `Append(uint seed, byte[], int, int)`) in `src/HddBackup.Core/Ext4/Crc32c.cs`
- [X] T014 [P] Implement `FileBlockDevice : IBlockDevice` (FileStream, read-only or read/write, 512-byte sectors, `Flush` → `Flush(true)`) in `src/HddBackup.Core/Devices/FileBlockDevice.cs`
- [X] T015 [P] Implement on-disk structures (superblock, 32/64-byte group descriptor, inode incl. extra fields, extent header/leaf/index/tail, dirent + csum tail, xattr header/entry, jbd2 superblock) with parse/serialize in `src/HddBackup.Core/Ext4/Ext4Structures.cs`
- [X] T016 [P] Implement feature flags and supported-set validation (R-2 table) returning unsupported feature names in `src/HddBackup.Core/Ext4/Ext4Features.cs`
- [X] T017 Implement `Ext4Reader`: superblock + checksum, group descriptors, inode read + checksum, extent tree walk (depth ≤ 5) and legacy block maps (direct, indirect, double, triple), directory iteration (linear; htree interior blocks skipped as fake dirents), xattrs (in-inode + external block + checksum), fast/slow symlinks, `Validate()` preconditions (R-3), `MeasureUsage()`. In `src/HddBackup.Core/Ext4/Ext4Reader.cs` (makes T008 pass)
- [X] T018 Implement `Ext4Layout`: mke2fs-equivalent geometry for a target size, block size, inode ratio/count, flex group size, and the reserved GDT blocks rule. It exposes per-group locations of superblock and GDT backups, bitmaps and inode tables. In `src/HddBackup.Core/Ext4/Ext4Layout.cs` (makes T009 pass)
- [X] T019 Implement `Ext4Writer` part 1, metadata skeleton: superblock (copy of template features, UUID, label, hash seed, checksum seed, timestamps), group descriptors, backups, bitmaps, zeroed inode tables with `itable_unused` and flags, special inodes 1/5/6, root (2) and lost+found (11). In `src/HddBackup.Core/Ext4/Ext4Writer.cs`
- [X] T020 Implement `Ext4Writer` part 2, content. Sequential block allocator, inode allocator, directory writer (linear blocks with csum tail, `.`/`..`, file types), regular files (data copied in large runs; extents in-inode or with one or two index levels; sparse holes preserved), symlinks (fast < 60 bytes, otherwise a block), device nodes, FIFOs and sockets, hard links (one inode, several dirents, correct link count), xattrs (in-inode first, then a shared external block de-duplicated by content). In `src/HddBackup.Core/Ext4/Ext4Writer.cs`
- [X] T021 Implement `Ext4Writer` part 3, finishing. Empty internal journal (inode 8, jbd2 v2 superblock with checksum v3, size = source journal size if it fits, otherwise the mke2fs default table), resize inode 7 (reserved GDT blocks, double-indirect map like mke2fs), final free counts, `dir_nlink` handling, and all checksums (bitmaps, descriptors, inodes, superblock). In `src/HddBackup.Core/Ext4/Ext4Writer.cs` (makes T010 pass)
- [X] T022 [P] Implement `Ext4Checker` (R-6 step 1) in `src/HddBackup.Core/Ext4/Ext4Checker.cs` (makes T011 pass)
- [X] T023 [P] Implement `Ext4TreeComparer` (R-6 step 2; hard links matched by link groups, not inode numbers) in `src/HddBackup.Core/Ext4/Ext4TreeComparer.cs` (makes T012 pass)
- [X] T024 Run `tests/fixtures/ext4/check-output.sh` on writer outputs produced by T010 (export them via a test helper flag `HDDBACKUP_EXPORT_DIR`) and fix every e2fsck finding. Record the result in `specs/002-ext4-image-shrink/quickstart.md` (Validation log section)

**Checkpoint**: the ext4 engine rebuilds every positive fixture; own checker, tree comparison and the independent `e2fsck -fn` are all clean.

---

## Phase 3: User Story 1 — Write an image to a smaller card (P1) 🎯 MVP

**Goal**: an oversized two-partition image is shrunk to fit the selected card and written in one confirmed flow.

**Independent Test**: on a 512 MiB fixture (ext4 used ≈ 60 MiB), "shrink and write" to a 256 MiB in-memory device. The device holds the original prefix, an updated MBR, and an ext4 that passes the checker and the tree comparison; the shrunk image file stays on disk.

### Tests

- [X] T025 [P] [US1] ShrinkPlanner tests: layout detection on fixtures, used size, minimum size within SC-005, and `Fits()` in `tests/HddBackup.Core.Tests/Shrinking/ShrinkPlannerTests.cs`
- [X] T026 [P] [US1] ImageShrinker end-to-end tests (raw and gzip sources). Output prefix bytes are identical except the root MBR entry, the new partition end matches the FS end, the checker and the comparer are clean, sidecars are correct, the source SHA-256 is unchanged, and no `.work.partial` or `.partial` file is left. In `tests/HddBackup.Core.Tests/Shrinking/ImageShrinkerTests.cs`
- [X] T027 [P] [US1] Engine tests for `OperationKind.ShrinkImage`: phases in order, percent < 100 until commit, cancellation at random points leaves no output and an unchanged source. In `tests/HddBackup.Core.Tests/Shrinking/ShrinkOperationTests.cs`
- [X] T028 [P] [US1] "Shrink and write" composition test: shrink then restore onto `MemoryBlockDevice` of the target size → device content equals the shrunk image. In `tests/HddBackup.Core.Tests/Shrinking/ShrinkAndWriteTests.cs`

### Implementation

- [X] T029 [P] [US1] Implement `ShrinkPlan`, `ShrinkBlocker`, `ShrinkRequest` per data-model.md in `src/HddBackup.Core/Shrinking/ShrinkPlan.cs`
- [X] T030 [US1] Implement `ShrinkPlanner.Analyze` and `Fits`: a raw source opened read-only via `FileBlockDevice`; a gzip source analyzed by streaming the prefix plus a temporary decompression, which is reused later as the working copy; layout checks, `Ext4Reader.Validate`, usage, and the minimum computation (R-4). In `src/HddBackup.Core/Shrinking/ShrinkPlanner.cs` (makes T025 pass)
- [X] T031 [US1] Implement `ImageShrinker`. Free-space check, gzip working copy, prefix copy, rebuild of the root FS into the output partition area, MBR entry update (GPT: entry, array CRC, header, and backup header/array at the new end), the three-step verification (R-6), the optional gzip output pass, `.partial` commit with checksum sidecars, and cleanup on failure or cancel. In `src/HddBackup.Core/Shrinking/ImageShrinker.cs` (makes T026 pass)
- [X] T032 [US1] Add `OperationKind.ShrinkImage`, request fields `ShrinkOutputPath` and `ShrinkTargetBytes`, phases `Analyzing`, `Rebuilding`, `Checking`, and engine wiring with `PhaseModel` weights (rebuild ≈ 80%, checks ≈ 20%) in `src/HddBackup.Core/Imaging/OperationRequest.cs`, `src/HddBackup.Core/Progress/OperationProgress.cs`, `src/HddBackup.Core/Progress/PhaseModel.cs`, `src/HddBackup.Core/Imaging/ImagingEngine.cs` (makes T027 pass)
- [X] T033 [US1] UI: when Write is refused for size, run `ShrinkPlanner.Analyze` (busy cursor) and show the offer dialog (image size, card size, used, result size, **Shrink and write** / **Cancel**). On accept, run ShrinkImage then RestoreDevice of the output through the normal confirm dialog, with combined progress and a banner naming both results. In `src/HddBackup.App/Forms/ShrinkOfferDialog.cs`, `src/HddBackup.App/Forms/MainForm.cs`, `src/HddBackup.App/Forms/OperationPanel.cs` (makes T028 meaningful end-to-end)
- [X] T034 [US1] Add US1 strings (contracts/ui.md: `Shrink_Offer*`, `Shrink_AndWrite`, `Shrink_ResultSaved`, `Phase_Analyzing`, `Phase_Rebuilding`, `Phase_Checking`, `Error_ShrinkVerifyFailed`) to all six `src/HddBackup.App/Resources/Strings*.resx`

**Checkpoint**: MVP — an image larger than a card can be written to it on Windows.

---

## Phase 4: User Story 3 — Refuse what cannot be shrunk safely (P1)

**Goal**: every unsafe source is refused with a specific, localized reason, and nothing is written.

**Independent Test**: each negative fixture produces its specific blocker; no output, working copy, or device write.

### Tests

- [X] T035 [P] [US3] Planner refusal tests on negative fixtures and synthetic cases: dirty FS, needs_recovery, orphan list, error state, unsupported feature (name reported), root not last, root not ext4, GPT with an extra partition, checksum mismatch, block size 8 K, a target smaller than the minimum. In `tests/HddBackup.Core.Tests/Shrinking/ShrinkRefusalTests.cs`
- [X] T036 [P] [US3] Safety tests: for every blocker, `ImageShrinker` throws before creating any file, and "shrink and write" never opens the device. In `tests/HddBackup.Core.Tests/Safety/ShrinkSafetyTests.cs`

### Implementation

- [X] T037 [US3] Complete blocker detection in `ShrinkPlanner` and `Ext4Reader.Validate` for all `ShrinkBlocker` values in `src/HddBackup.Core/Shrinking/ShrinkPlanner.cs` and `src/HddBackup.Core/Ext4/Ext4Reader.cs` (makes T035/T036 pass)
- [X] T038 [US3] UI: when no offer is possible, show the blocker list (localized; the feature name verbatim) in the size-refusal message and in the Shrink dialog. In `src/HddBackup.App/Forms/ShrinkOfferDialog.cs` and `src/HddBackup.App/Forms/MainForm.cs`
- [X] T039 [US3] Add `Blocker_*` strings to all six `src/HddBackup.App/Resources/Strings*.resx`
- [X] T040 [US3] Extend Help → Device diagnostics: when an image is selected on the Restore tab, append the ShrinkPlan summary (layout, features, used, minimum, blockers). In `src/HddBackup.App/Forms/DiagnosticsDialog.cs`

**Checkpoint**: unsafe images are always refused with a clear reason.

---

## Phase 5: User Story 2 — Shrink an image without writing it (P2)

**Goal**: standalone "Shrink image…" to the smallest size or to a card size, with raw or gzip output.

**Independent Test**: shrink a fixture to "Smallest possible" and to "Fit 300 MB" without any device; outputs pass the checker and comparer, and the sizes match the requests.

### Tests

- [X] T041 [P] [US2] Target-size tests: minimum within SC-005; a fixed target respected; a target below the minimum refused; `.img.gz` output decompresses to the same bytes as the raw output. In `tests/HddBackup.Core.Tests/Shrinking/ShrinkTargetTests.cs`

### Implementation

- [X] T042 [US2] Implement `ShrinkDialog` (summary, target radio: minimum or card size in MB/GB, output path with `.img`/`.img.gz`, blockers, Start/Cancel) in `src/HddBackup.App/Forms/ShrinkDialog.cs`
- [X] T043 [US2] Add a **Shrink image…** button to the Restore tab that opens `ShrinkDialog` and starts `OperationKind.ShrinkImage` through `MainForm.StartOperation`. In `src/HddBackup.App/Forms/RestoreTab.cs` and `src/HddBackup.App/Forms/MainForm.cs`
- [X] T044 [US2] Add US2 strings (`Restore_Shrink`, `Shrink_Title`, `Shrink_Summary`, `Shrink_Minimum`, `Shrink_Target*`, `Shrink_Output`, `Shrink_Start`) to all six `src/HddBackup.App/Resources/Strings*.resx`

**Checkpoint**: images can be shrunk for archiving or later use.

---

## Phase 6: Polish & Cross-Cutting

- [X] T045 [P] Performance: shrink a synthetic 16 GiB image with 3 GiB of data (generated by `make-fixtures.sh --large`, not committed) and record the time against SC-004. Tune copy buffer sizes in `src/HddBackup.Core/Ext4/Ext4Writer.cs`
- [X] T046 [P] Add the hardware steps from quickstart.md (Raspberry Pi boot from a shrunk image, `e2fsck -fn` on the device) to `docs/manual-hardware-checklist.md`
- [X] T047 [P] Update `README.md` (feature description, limits, supported ext4 features) and `docs/safety-review.md` (shrink paths: original never modified, three-step verification)
- [X] T048 Screenshot check of the offer dialog and the Shrink dialog in en/de/ja for clipping (as done for feature 001)
- [X] T049 Run the full test suite, `tests/check-artifact.ps1` (exe < 5 MB) and `check-output.sh` on all outputs; record the results in `docs/release-notes.md`

---

## Dependencies & Execution Order

- **Setup (T001–T005)** → **Foundational (T006–T024)** → user stories.
- **US1 (T025–T034)** needs Foundational. **US3 (T035–T040)** needs the planner from US1 (T029–T030). **US2 (T041–T044)** needs the engine operation from US1 (T031–T032).
- Within Foundational: T013–T016 in parallel → T017 → T018 → T019 → T020 → T021 → T022/T023 in parallel → T024.
- `Ext4Writer.cs` (T019–T021) and `ImagingEngine.cs` (T032) are single files and therefore sequential.

### Parallel Opportunities

- T002, T003, T005; all Foundational tests T006–T012; T013–T016; T022 + T023.
- US1 tests T025–T028; US3 tests T035–T036; polish T045–T047.

## Parallel Example: Foundational

```text
Task: "Crc32c in src/HddBackup.Core/Ext4/Crc32c.cs"
Task: "FileBlockDevice in src/HddBackup.Core/Devices/FileBlockDevice.cs"
Task: "On-disk structures in src/HddBackup.Core/Ext4/Ext4Structures.cs"
Task: "Feature validation in src/HddBackup.Core/Ext4/Ext4Features.cs"
```

## Implementation Strategy

1. Setup and fixtures (needs Docker once on the developer machine).
2. Foundational engine until `e2fsck -fn` is clean on all fixtures (**the critical risk**; stop
   and reassess if the independent check cannot be made clean).
3. US1 (MVP): shrink and write.
4. US3: refusals (could be pulled before US1's UI if preferred, since it shares the planner).
5. US2: standalone dialog.
6. Polish, performance, hardware test on a Raspberry Pi.

## Implementation notes (deviations from the plan above)

- Shrink tests live in one file, `tests/HddBackup.Core.Tests/Shrinking/ShrinkTests.cs`
  (classes `ShrinkPlannerTests`, `ImageShrinkerTests`, `ShrinkRefusalTests`,
  `ShrinkAndWriteTests`). They cover T025–T028, T035–T036 and T041. The SC-004 benchmark is
  `ShrinkPerformanceTests.cs`, which is opt-in via `HDDBACKUP_PERF_IMAGE`.
- Checksums: `Checksums.cs` (`Crc32c`, plus `Crc16` for `uninit_bg`) instead of `Crc32c.cs`.
- The shrink operation reuses the existing phases (`Preparing` = unpack/analyze,
  `Transferring` = rebuild, `Verifying` = checks, `Flushing` = save) with shrink-specific
  `Phase_Shrink_*` texts, instead of new `Analyzing`/`Rebuilding`/`Checking` phases.
- The performance image is built by `tests/fixtures/ext4/make-large.sh` rather than a
  `--large` switch.
- `ShrinkPlan.Describe()` provides the diagnostics summary (T040), shown for the full-device
  image selected on the Restore tab.

## Notes

- [P] = different files, no dependencies on incomplete tasks.
- Never test writes against real disks except dedicated expendable media.
- The product never invokes Linux tools; Docker is developer tooling only.
