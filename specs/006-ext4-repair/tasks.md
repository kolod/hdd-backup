---

description: "Task list for feature 006: ext4 repair after an unclean shutdown"
---

# Tasks: Repair of ext4 File Systems After an Unclean Shutdown

**Input**: Design documents from `/specs/006-ext4-repair/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md

**Tests**: Included — constitution Principle VI requires test-verified disk I/O, and the spec's
success criteria (SC-001–SC-003) are defined against fixtures and an independent Linux check.

**Organization**: US1 repair an image (P1), US2 refuse damage beyond power loss (P1), US3 repair
while shrinking / shrink-and-write (P2).

## Format: `[ID] [P?] [Story] Description`

- Paths: `src/HddBackup.Core/`, `src/HddBackup.App/`, `tests/HddBackup.Core.Tests/`,
  `tests/fixtures/ext4/`
- Strings: every new key goes into all six `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx`
- The constitution amendment 1.2.0 is already applied (commit d3135db).

---

## Phase 1: Setup — damaged fixtures

- [ ] T001 Start Docker and confirm the journal commands of e2fsprogs 1.47 inside `debian:bookworm` (`debugfs -w -R "help"` lists `journal_open`, `journal_write`, `journal_close`); record the result in `specs/006-ext4-repair/research.md` R-7 (if missing, use the fallback: a Python script `tests/fixtures/ext4/write-journal.py` that writes jbd2 v3 descriptor/data/commit blocks with crc32c checksums into the journal inode's blocks, located with `debugfs -R "blocks <8>"`)
- [ ] T002 Extend `tests/fixtures/ext4/make-fixtures.sh` to build, from the existing `rpi-like-4k` staging tree, five damaged images plus references, written to `tests/HddBackup.Core.Tests/Fixtures/ext4/`: `dirty-journal.img.gz` (several transactions written with `journal_write` for changed file data, a new file, a deleted file and their metadata blocks; `needs_recovery` set; the on-disk blocks left old), `dirty-journal-torn.img.gz` (same, last commit block zeroed), `orphans.img.gz` (two unlinked inodes with `links_count 0` chained via `i_dtime` from `s_last_orphan`, one truncate orphan with blocks beyond `i_size`), `not-clean.img.gz` (`ssv state 0`, wrong superblock and group 0 free counts, one block bitmap bit cleared for a used block), `damaged-dirent.img.gz` (a directory entry pointing to an inode cleared with `clri`); for each of the first four also `<name>.reference.manifest.tsv` = manifest of the tree after `e2fsck -fy` on a copy (same manifest format as existing fixtures)
- [ ] T003 Extend `tests/fixtures/ext4/check-output.sh` with a mode `--reference <manifest>` that compares the repaired output's tree to a reference manifest instead of to a source image; keep `e2fsck -fn` must exit 0
- [ ] T004 Run `make-fixtures.sh` in Docker, add the new fixture files to the repository (`.gitattributes` already marks `*.img.gz` binary and `*.tsv` -text), and add the names to `tests/HddBackup.Core.Tests/Fixtures/FixtureImages.cs` if it lists fixtures explicitly

---

## Phase 2: Foundational — journal overlay, orphans, finding kinds

- [ ] T005 [P] Create `src/HddBackup.Core/Ext4/Jbd2Journal.cs`: `internal sealed class Jbd2Journal` with `static Jbd2Journal Open(Ext4Reader reader)` (reads the journal inode `s_journal_inum`, maps journal block numbers to fs blocks through its extents; refuses external journals and non-empty fast-commit areas with `Ext4FormatException`), journal superblock parsing (big-endian fields: magic 0xC03B3998, block type, sequence, blocksize, maxlen, first, s_sequence, s_start, feature compat/incompat/ro_compat, UUID, checksum type, checksum), and `JournalOverlay Replay()` implementing research R-2: scan from `s_start` with expected sequence, descriptor tags (32/64-bit block numbers, tag3/tag size for CSUM_V2/V3, `SAME_UUID`, `LAST_TAG`, `ESCAPE` restores 0xC03B3998 in the first 4 bytes), revoke blocks (32/64-bit), commit blocks (checksums for v2/v3 and async commit), crc32c checksums seeded with `crc32c(~0, journal UUID)` per jbd2 rules; a transaction is applied only when its commit block is present and all checksums match; the first incomplete or invalid transaction ends the scan; returns `JournalOverlay { IReadOnlyDictionary<long, byte[]> Blocks, int Replayed, int Discarded, bool WasEmpty }`
- [ ] T006 [P] Create `src/HddBackup.Core/Ext4/OverlayBlockDevice.cs`: read-only `IBlockDevice` over an inner device (the root partition view) and a `JournalOverlay` with the fs block size; `Read` serves overlay blocks for every fs block they cover and the inner device elsewhere (reads may span blocks and be unaligned to fs blocks but aligned to the sector size); `Write` throws
- [ ] T007 [P] Create `src/HddBackup.Core/Ext4/OrphanList.cs`: `static OrphanList Read(Ext4Reader reader)` walking `s_last_orphan` → `i_dtime` chain with loop and range checks (a broken chain is an `Ext4FormatException`); exposes `IReadOnlyCollection<uint> Deleted` (links_count 0) and `IReadOnlyDictionary<uint, long> Truncated` (links_count > 0 → `i_size`); refuses `INCOMPAT_ORPHAN_FILE` unless empty
- [ ] T008 Change `src/HddBackup.Core/Ext4/Ext4Checker.cs` to return `List<Ext4Finding>` (`Ext4Finding { Ext4FindingKind Kind; string Message; }`, `Ext4FindingKind { PowerLoss, Structural }`, new file `src/HddBackup.Core/Ext4/Ext4Finding.cs`) with kinds per research R-4 (PowerLoss: inode/block bitmap entries and bitmap checksums, group/superblock free counts, group directory counts, used-but-unreachable inodes, "journal is not empty"; everything else Structural, including format exceptions); update existing callers (`ImageShrinker`, `DiagnosticsDialog`/`ShrinkPlan.Describe` if they use the strings) to use `.Message`, keeping behavior
- [ ] T009 Change `Ext4CopyPlan.Build` in `src/HddBackup.Core/Ext4/Ext4CopyPlan.cs` to take an optional `OrphanList`: inodes in `Truncated` copy only the blocks covering `i_size` (and their extent tree accordingly), `Deleted` inodes are never reached through the tree (assert they are not; if one is reachable, throw `Ext4FormatException` — that is structural damage)
- [ ] T010 [P] Create `tests/HddBackup.Core.Tests/Ext4/Jbd2JournalTests.cs`: empty journal → no overlay; `dirty-journal` fixture → overlay contains the expected blocks and `Replayed` equals the number of written transactions; `dirty-journal-torn` → last transaction discarded; synthetic in-memory journals for escape flag, revoke (block revoked in a later transaction not replayed from earlier ones), 32-bit vs 64-bit tags, wrong descriptor checksum ends the scan
- [ ] T011 [P] Create `tests/HddBackup.Core.Tests/Ext4/OverlayAndOrphanTests.cs`: reading the `dirty-journal` root partition through `OverlayBlockDevice` gives the tree of the reference manifest (via `Ext4Reader` + manifest compare helper used by existing fixture tests); `OrphanList` on `orphans` returns the two deleted inodes and the truncate orphan with its size; a looping orphan chain throws
- [ ] T012 [P] Create `tests/HddBackup.Core.Tests/Ext4/CheckerKindTests.cs`: `not-clean` and the overlaid `dirty-journal`/`orphans` fixtures give only `PowerLoss` findings; `damaged-dirent` gives at least one `Structural` finding; clean fixtures give none
- [ ] T013 Run T010–T012 and fix T005–T009 until they pass

**Checkpoint**: the recovered file system of each power-loss fixture can be read and classified.

---

## Phase 3: User Story 1 - Repair an image (Priority: P1) 🎯 MVP

**Goal**: "Same as the image" + **Repair the file system** produces a clean, verified copy.

**Independent Test**: repair each power-loss fixture; own check clean, tree equals the e2fsck
reference manifest, independent `e2fsck -fn` clean.

- [ ] T014 [US1] In `src/HddBackup.Core/Shrinking/ShrinkPlan.cs` add `FileSystemState` per data-model.md (`PartitionNumber`, `Condition`, `RepairReasons` with orphan count, `Problems`, `IsRoot`) and `IReadOnlyList<FileSystemState> FileSystems` on `ShrinkPlan`; add `ShrinkBlocker.NeedsRepair`
- [ ] T015 [US1] In `src/HddBackup.Core/Shrinking/ShrinkPlanner.cs` `Analyze`: for each ext4 partition read through `Jbd2Journal` + `OverlayBlockDevice` (gzip sources: as today via the sample device for the table/superblock; the full state for the root partition is computed in the rebuild, so the planner reports `NeedsRepair` from the superblock flags and orphan head, and `Damaged` only when known from the sample), fill `FileSystems`; journal/not-clean/orphans no longer produce their old blockers but `NeedsRepair` (repair reasons kept); recorded errors stay a blocker (`FsHasErrors`); used data and minimum size are computed on the overlaid source when it is available
- [ ] T016 [US1] In `src/HddBackup.Core/Shrinking/ImageShrinker.cs` add `ShrinkRequest.Repair`; in `Rebuild` wrap `sourceRoot` in `OverlayBlockDevice(Jbd2Journal.Open(...).Replay())` when `Repair`, read `OrphanList`, run `Ext4Checker` on the overlaid source and throw `Blocked(... Structural findings ...)` (new `ShrinkBlocker.StructuralDamage` with the first findings as detail) when any `Structural` finding exists; skip the `needs_recovery`/`not_clean`/`orphans` problems of `reader.Validate()` in repair mode; pass the `OrphanList` to `Ext4CopyPlan.Build`; allow `TargetImageBytes` equal to the source image size (no `AlreadyFits` refusal) when `Repair`; compare the output tree against the overlaid source (existing `Ext4TreeComparer`, which then naturally excludes orphans); report `RepairSummary { JournalTransactions, OrphansRemoved }` in `ShrinkOutcome`
- [ ] T017 [US1] In `src/HddBackup.Core/Imaging/OperationRequest.cs` add `RepairFileSystem`; in `src/HddBackup.Core/Imaging/ImagingEngine.cs` pass it as `ShrinkRequest.Repair`, let `ValidateShrink` accept `NeedsRepair` plans when `RepairFileSystem` and a same-size target, and copy `RepairSummary` into `OperationResult` (`RepairJournalTransactions`, `RepairOrphansRemoved` in `src/HddBackup.Core/Imaging/OperationResult.cs`); add `ShrinkOutputName.Repaired(source, targetBytes, gzip)` → `<name>_repaired[_shrunk_<N>G].img[.gz]` in `src/HddBackup.Core/Shrinking/ShrinkOutputName.cs`
- [ ] T018 [P] [US1] Create `tests/HddBackup.Core.Tests/Shrinking/RepairTests.cs`: for `dirty-journal`, `dirty-journal-torn`, `orphans`, `not-clean` — repair at the same size (`ShrinkImage`, `RepairFileSystem`, target = source size) → completed, own `Ext4Checker` returns no findings, output tree equals `<name>.reference.manifest.tsv`, partitions before the root byte-identical to the source, source unchanged, summary counts as expected; without `RepairFileSystem` the same images are refused with `NeedsRepair`; cancellation leaves no files; gzip source and gzip output work
- [ ] T019 [US1] Run T018 and fix until it passes; run the full test suite
- [ ] T020 [P] [US1] Add strings to all six `.resx`: `Image_FileSystemHeading` "File system", `Image_FsClean` "{0}: clean", `Image_FsNeedsRepair` "{0}: needs repair — {1}", `Image_FsDamaged` "{0}: cannot be repaired by this program — {1}. Repair it with a Linux file system check and back up again.", `Reason_Journal` "the journal needs recovery", `Reason_Orphans` "{0} orphan inodes", `Reason_NotClean` "not cleanly unmounted", `Group_FileSystem` "File system", `Image_Repair` "Repair the file system", `Tooltip_Repair` "Applies the journal, frees orphan inodes, corrects free space counts and marks the file system clean", `Image_RepairRequired` "The file system needs repair before it can be shrunk.", `Phase_Repair_Journal` "Replaying the journal…", `Result_Repaired` "Repaired image saved: {0}", `Result_RepairDone` "The file system was repaired: {0} journal transactions applied, {1} orphan inodes removed."
- [ ] T021 [US1] In `src/HddBackup.App/Forms/ImageTab.cs`: "File system" heading and lines in the left area from `ShrinkPlan.FileSystems` (partition label "#{n} {name} (ext4)"); right column `Group_FileSystem` + check box `_repair` (`Image_Repair`, tooltip) shown only when the root partition `NeedsRepair`, checked by default when it appears; with `_same` + `_repair` checked: request `ShrinkImage` with `ShrinkTargetBytes = source image bytes (plan.SourceImageBytes or file size for raw)`, `RepairFileSystem = true`, any output format (no `Image_SameAsSource` reason), default name `ShrinkOutputName.Repaired(source, null, gzip)`; with shrink sizes: `RepairFileSystem = _repair.Checked`, shrink unavailable with `Image_RepairRequired` when the root needs repair and `_repair` is unchecked; red damaged line and shrink unavailable when `Damaged`
- [ ] T022 [US1] In `src/HddBackup.App/Forms/OperationPanel.cs`: Preparing label `Phase_Repair_Journal` when the request has `RepairFileSystem` and the phase is Preparing before Transferring; result lines `Result_Repaired` (same-size repair) and `Result_RepairDone` with the counts
- [ ] T023 [US1] Build; run quickstart scenarios 1–3 with the in-process harness approach of feature 005 and record results

**Checkpoint**: MVP — repaired copy from the Image tab.

---

## Phase 4: User Story 2 - Refuse damage beyond power loss (Priority: P1)

**Goal**: structural damage refused before writing, with the problems listed.

**Independent Test**: `damaged-dirent` is refused with a reason and no output file.

- [ ] T024 [P] [US2] Add to `tests/HddBackup.Core.Tests/Shrinking/RepairTests.cs`: `damaged-dirent` with `RepairFileSystem` → `ShrinkNotPossible` with `StructuralDamage` and the finding text, no output or partial files; a fixture with the recorded-errors flag (`ssv state 2` on a copy made in the test via raw superblock edit) → refused with `FsHasErrors`; a forced verification failure (output check mocked to fail via an internal test hook) → nothing kept
- [ ] T025 [US2] Add `Blocker_StructuralDamage` "The file system has damage that is not caused by an unclean shutdown: {0}" and `Blocker_NeedsRepair` "The file system needs repair (unclean shutdown)." to all six `.resx`, and map the new blockers in `src/HddBackup.App/Localization/Formatters.cs` `Blockers`
- [ ] T026 [US2] Image tab: for `Damaged`, show `Image_FsDamaged` in dark red with up to three problems; hide `_repair`; shrink sizes unavailable with the same text; Restore tab warning uses the damage text (via `Formatters.Blockers`); run quickstart scenario 6

---

## Phase 5: User Story 3 - Repair while shrinking and in "Shrink and write" (Priority: P2)

**Goal**: one operation repairs and shrinks; Restore tab flow repairs automatically.

**Independent Test**: shrink `orphans` and `dirty-journal` to the smallest size with repair; clean,
reference tree, smaller result.

- [ ] T027 [P] [US3] Add to `tests/HddBackup.Core.Tests/Shrinking/RepairTests.cs`: repair + smallest size and repair + 1 GiB card for `dirty-journal` and `orphans` → completed, smaller than the source, own check clean, tree equals the reference; `ImageFit.Evaluate` returns `ShrinkToFit` for a `NeedsRepair` plan whose minimum fits
- [ ] T028 [US3] In `src/HddBackup.Core/Shrinking/ImageFit.cs` treat a plan whose only blockers are `NeedsRepair` as shrinkable (`ShrinkToFit` / `CannotFit` by size), and expose `NeedsRepair` on the result (e.g. `ImageFit.RequiresRepair(plan)`) for the UI
- [ ] T029 [P] [US3] Add strings `Restore_TooLargeRepair` "The image is larger than the device. Its file system will be repaired and the image shrunk to fit before writing." and `Shrink_OfferRepair` "The file system will be repaired (unclean shutdown) as part of the shrink." to all six `.resx`
- [ ] T030 [US3] `src/HddBackup.App/Forms/RestoreTab.cs`: use `Restore_TooLargeRepair` when the plan needs repair; `src/HddBackup.App/Forms/ShrinkOfferDialog.cs`: add the `Shrink_OfferRepair` line when the plan needs repair; `src/HddBackup.App/Forms/MainForm.cs` `OfferShrinkIfTooLargeAsync`: pass `RepairFileSystem = true` in the shrink request when the plan needs repair and name the output with `ShrinkOutputName.Repaired` (still through `ImageFileName.FirstFree`)
- [ ] T031 [US3] Build; run quickstart scenarios 4, 5, 7 and record results

---

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T032 Run the independent check in Docker for every repaired fixture output (`check-output.sh --reference`), add the repair outputs to the `ext4-check` CI job script so CI runs it on each PR, and record results in `specs/006-ext4-repair/quickstart.md`
- [ ] T033 Performance: repair of the 16 GiB `make-large.sh` image (made dirty with a journal transaction) versus shrinking it (`ShrinkPerformanceTests` pattern, opt-in via environment variable); record times (SC-004)
- [ ] T034 [P] Key check (all used keys exist in six languages, no unused static keys), screenshots of the Image tab with a `NeedsRepair` and a `Damaged` image in six languages and de/ja at 800 × 560
- [ ] T035 [P] Update `README.md` (repair on the Image tab, scope and limits), `docs/release-notes.md` (Unreleased), `docs/safety-review.md` (repair safety: images only, overlay read-only, structural refusal, verification), `docs/manual-hardware-checklist.md` (back up a card from a device switched off by pulling the power, repair, write back, boot, `fsck` on the device clean)

---

## Dependencies & Execution Order

- Phase 1 (fixtures) → Phase 2 (overlay, orphans, kinds) → US1 → US2/US3.
- US2 builds on the repair path of US1 (refusal happens in the same rebuild); US3 builds on US1's
  shrinker flag.
- Resource tasks (T020, T025, T029) edit the same six files: one after another.
- `ImageTab.cs` changes (T021, T026) are sequential.

### Parallel Opportunities

- T005, T006, T007 in parallel (different files); T010, T011, T012 in parallel after them.
- T018 (tests) in parallel with T020 (strings).
- T024 and T027 tests in parallel with UI tasks.
- T034 and T035 in parallel.

## Parallel Example: Phase 2

```text
Task: "T005 Jbd2Journal.cs"
Task: "T006 OverlayBlockDevice.cs"
Task: "T007 OrphanList.cs"
```

## Implementation Strategy

1. Fixtures first (Phase 1): the Linux reference defines correctness.
2. Phase 2: journal overlay and orphans proven against the references.
3. US1 (MVP): same-size repair end to end, verified by own check, reference tree and e2fsck.
4. US2: refusals. 5. US3: repair + shrink and shrink-and-write.
6. Polish: CI independent check, performance, docs, hardware step.
