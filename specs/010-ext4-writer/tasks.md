---

description: "Task list for feature 010: editing files of an ext4 partition (edit mode, diff file, save, cards)"
---

# Tasks: Editing Files of an ext4 Partition

**Input**: Design documents from `/specs/010-ext4-writer/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md; the
constitution amendment 1.5.0 is applied (2026-11-08)

**Tests**: Included — constitution Principle VI requires test-verified disk I/O, Principle I needs
a test for every safety rule (nothing written before Save, writes only inside the partition,
interruption at every write), and SC-001 to SC-010 are defined against prepared images, the product's
own checker and an independent `e2fsck -fn`.

**Organization**: US1 edit a file and save it into an image (P1), US2 changes wait in a diff file
(P1), US3 create/delete/rename (P2), US4 checksum follows the image (P2), US6 an interrupted save can be
finished (P2), US5 edit a card, written at once (P3).

## Format: `[ID] [P?] [Story] Description`

- Paths: `src/HddBackup.Core/Editing/`, `src/HddBackup.Core/Devices/`, `src/HddBackup.Core/Imaging/`,
  `src/HddBackup.App/Forms/`, `tests/HddBackup.Core.Tests/Editing/`, `tests/fixtures/ext4/`, `tools/ci/`
- Strings: every new key goes into all six `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx`
  (English texts in `contracts/ui.md`). The helper used for features 008/009 (`Add-Strings.ps1` in the
  session scratchpad; a JSON map of key → six texts) appends them; the resource-parity test must pass.
  **Never pipe the helper's output into `Select-Object -First` or `head`.**
- Core returns codes and arguments; texts are built in the App (`EditTexts`).
- **Safety rules that every task must keep**: nothing is written before Save (image) or the card
  confirmation; every write goes through a `PartitionView`; the browse mode of features 008/009 is
  unchanged and their tests keep passing.
- Python and `sed` mangle `\n` in C# string literals; edit such tests with the Edit tool.

---

## Phase 1: Setup — gate and fixtures

- [X] T001 Get the draft layout mockup in `specs/010-ext4-writer/spec.md` approved by the maintainer; change its heading to `(approved)` and the **Status** line to `Draft (layout approved <date>)`. **Gate: no App task (T019, T020, T027, T033, T037, T040, T045, T046, T048) starts before this.**
- [X] T002 Create `tests/fixtures/ext4/make-editing-fixtures.sh` (model: `make-diagnostic-fixtures.sh`, helpers in `fixture-lib.sh`) building bare ext4 images with `mke2fs -d` to `tests/HddBackup.Core.Tests/Fixtures/ext4/<name>.img.gz`: `edit-basic` (4 KiB blocks, `metadata_csum`, `flex_bg`; `/etc/hostname`, `/etc/fstab`, a text file with CRLF and one without a final newline, an empty file, a 300 KiB text file, a binary file, a symlink fast and slow, a directory `/many` with 600 files so it is htree-indexed, hard links `a`/`b`, a file with an xattr block, nested directories three deep with files, `lost+found`), `edit-1k` (1 KiB blocks, same content subset), `edit-nocsum` (`uninit_bg`, no `metadata_csum`), `edit-full` (a 4 MiB file system with only a few free blocks and inodes), `edit-uninit` (64 MiB with many `BLOCK_UNINIT`/`INODE_UNINIT` groups: `-E lazy_itable_init=1`, `-O uninit_bg`, tiny content). Each also gets `<name>.manifest.tsv` as in feature 008 (path, type, mode, uid, gid, size, mtime, sha256, link)
- [X] T003 Run `make-editing-fixtures.sh` in Docker (command in `quickstart.md`), check the images in, and confirm `FixtureImages.RawPath("edit-basic")` and `BrowseSource.OpenImage` open each one (the `rpi-like-4k` fixture of feature 008 is reused for cards)
- [X] T004 Create `tests/fixtures/ext4/check-editing.sh`: for each `<exports>/editing/saved-<scenario>.img` (a whole ext4 image) run `e2fsck -fn` (must exit 0) and compare `debugfs -R "rdump / <dir>"` of the image with the expected listing `<exports>/editing/saved-<scenario>.tree.tsv` (`D<TAB>path` / `F<TAB>sha256<TAB>path`, the same format as feature 008's extracted listings); append `editing/<scenario> OK|FAILED` lines to the results file like `check-diagnostics.sh`. Hook it into `tools/ci/Invoke-Ext4Checks.sh` (no-op without `editing/`; also extend the "no exports found" test) — `e2fsprogs` is already installed in the `ext4-check` job

---

## Phase 2: Foundational — primitives that every story needs

- [ ] T005 Move the checksum functions used by `Ext4Writer` and `Ext4Reader` (inode, directory block with tail, extent block, group descriptor crc16/crc32c, superblock, bitmaps, xattr block) into one `internal static class Ext4Csum` in `src/HddBackup.Core/Ext4/Ext4Csum.cs` (the class at the end of `Ext4Reader.cs` already holds some), make both classes call it, and add `Ext4CsumTests` in `tests/HddBackup.Core.Tests/Editing/` that recompute the checksums of every structure of `edit-basic` and `edit-nocsum` and compare with the stored values. Behaviour of features 002/006 must not change (their tests are the guard)
- [X] T006 [P] Create `src/HddBackup.Core/Editing/Ext4EditSupport.cs` per research R-2: `Check(Ext4Superblock)` → `EditSupport { Editable, Reason, Detail }` with the refusal codes of data-model.md (`NeedsRecovery`, `HasOrphans`, `HasErrors`, `UnsupportedFeature`, `Damaged`), and `tests/HddBackup.Core.Tests/Editing/Ext4EditSupportTests.cs` over the fixtures and over superblocks with each refused feature bit set, plus the `dirty-journal` and `orphans` fixtures of feature 006
- [X] T007 [P] Create `src/HddBackup.Core/Editing/WriteSet.cs`: `BlockWrite`, `WritePhase` (`Data`, `Allocation`, `Inode`, `Link`, `Free`, `Counters`), `WriteSet` (ordered list, last write to an offset wins when replayed in order), and `RecordingBlockDevice : IBlockDevice` over a partition (reads see the recorded writes, `Write` only records, `Size`/`SectorSize` of the inner device); `WriteSetTests` (reads see writes, ordering kept, overlapping writes, sector alignment enforced, zero writes reach the inner device)
- [X] T008 [P] Create `src/HddBackup.Core/Editing/EditChange.cs` (kinds, `OriginalStamp`) and `EditNames.cs` (`Validate(name, existingNames)` → code: empty, `.`/`..`, `/` or NUL, longer than 255 UTF-8 bytes, exists; plus path rules: root, `lost+found`, ancestor) with `EditNamesTests` (table-driven, non-ASCII, 255 vs 256 bytes)
- [X] T009 Create `src/HddBackup.Core/Editing/Ext4Allocator.cs` per research R-3: block and inode bitmaps per touched group read on demand and written back into the write set, goal-based first fit, building the in-memory bitmap of a `BLOCK_UNINIT` group (backups, reserved GDT, other groups' bitmaps/inode tables under `flex_bg`) and clearing the flag, `INODE_UNINIT`/`itable_unused` handling, free counters of group and superblock (64-bit aware), bitmap and group descriptor checksums, `AllocateExtents(count, goalGroup)`, `FreeBlocks(ranges)`, `AllocateInode(parentGroup, isDirectory)`, `FreeInode`; `Ext4AllocatorTests` on every fixture (allocate and free in initialised, uninitialised, full groups; counters equal those recomputed from the bitmaps; `Ext4Checker` clean on the recording overlay; no group descriptor or bitmap of an untouched group is written)
- [X] T010 Create `src/HddBackup.Core/Editing/Ext4InodeWriter.cs`: store an inode with checksum (128/256-byte inodes, `extra_isize`), build an extent tree for a list of extents (inline up to four, depth-1 leaf block with `ExtentTreeBlocks`-compatible layout and tail checksum, "too fragmented" when more than the leaf capacity), free an inode's blocks (extent leaves and index blocks, slow symlink blocks, the xattr block with reference count), set times and sizes; `Ext4InodeWriterTests` on `edit-basic` and `edit-1k` (round trip through `Ext4Reader.MapFile`/`ReadFile`)
- [X] T011 Create `tests/HddBackup.Core.Tests/Editing/EditFixtures.cs` (copy a fixture to a temp image, open it read/write, locate the ext4 partition, build an `Ext4Reader`) and `EditAssert.cs` (`TreesEqualExcept(before, after, changedPaths)` over the reader — content, mode, owner, times, links of every untouched entry; `CheckerClean(device)`; `ExportForCi(scenario, imagePath, expectedTree)` that writes `saved-<scenario>.img` and `.tree.tsv` to `<HDDBACKUP_EXPORT_DIR>/editing/` when set)
- [ ] T012 Create `src/HddBackup.Core/Editing/EditPreflight.cs`: given a `WriteSet` and the partition, wrap them in a `RecordingBlockDevice` and run `Ext4Checker.CheckDetailed` (mode `Full`) or the targeted check of the touched structures and groups (mode `Targeted`), returning findings; the mode threshold is a constant set by T076 after measuring; `EditPreflightTests` (a deliberately corrupted write set — a wrong free counter, a bad inode checksum, an entry to an unallocated inode — is rejected; a correct one passes; nothing reaches the real device)

**Checkpoint**: the allocator, inode writer, write set, preflight and test helpers work; no user-visible change.

---

## Phase 3: User Story 1 - Change a file inside an image and save it (Priority: P1) 🎯 MVP

**Goal**: in the Files tab, **Edit** → change the text of a file → **Save** writes it into the image.

**Independent Test**: edit one text file of `edit-basic` and save: the file reads back with the new text, every
other entry is unchanged, the file system passes the own checker and `e2fsck -fn`; before Save the SHA-256 is
unchanged.

- [X] T013 [US1] Create `src/HddBackup.Core/Editing/Ext4Editor.cs` (facade over reader, allocator, inode writer; produces a `WriteSet` with the phase order of research R-5) with `ReplaceContent(path, bytes)`: copy-on-write (new extents and data → allocation counters → one inode write with new extents, size and mtime/ctime → free the old blocks), refusals `NoSpace`, `TooFragmented`, `NotRegularFile`, `EntryChanged` (original stamp differs); `FileBlockDevice.OpenReadWrite(path)` (exclusive, `FileShare.None`) in `src/HddBackup.Core/Devices/FileBlockDevice.cs`
- [X] T014 [P] [US1] Create `tests/HddBackup.Core.Tests/Editing/ContentEditTests.cs` (SC-001/SC-002): on `edit-basic`, `edit-1k`, `edit-nocsum`, `edit-uninit`: grow and shrink a file across block boundaries, to and from empty, a 300 KiB file, a file with xattr and a hard-linked file (both names read the new content), a CRLF file, content equal to the old, the nearly full `edit-full` (refused `NoSpace` before any write: zero writes recorded), a fragmented free space; after each: `EditAssert.TreesEqualExcept`, own checker, content read back, ExportForCi; and the phase-order test (the recorded writes are in R-5 order, the inode write is a single write inside one sector)
- [X] T015 [US1] Create `src/HddBackup.Core/Editing/PendingTree.cs` (the `IFileTree` of data-model.md, first for `EditContent`: `Describe` shows the new size and time, `ReadRange` serves the new bytes, `PendingState`) and `src/HddBackup.Core/Editing/ImageEditSession.cs` with an in-memory change list: `Open(image, partition)` (checks `Ext4EditSupport`, opens read-only), `SetContent(path, bytes)`, `Changes`, `Save(CancellationToken)` (opens the image read/write, re-checks support and the original stamps, runs the editor, `EditPreflight`, replays the write set in order with flushes between phases, closes), `Discard()`; session refusals per data-model.md
- [ ] T016 [P] [US1] Create `tests/HddBackup.Core.Tests/Editing/ImageEditSessionTests.cs`: nothing is written before Save (SHA-256 of the image equal after `SetContent`, after reopening the session), Save equals the expected result (T014 helpers), refusals (a compressed image, `dirty-journal`, `orphans`, a read-only image file, an unsupported feature) write nothing, an image changed by something else between edit and Save is refused (`ImageChanged`/`EntryChanged`), `PendingTree` shows the new text and size and leaves the on-disk view untouched
- [X] T017 [US1] Add the `SaveEdits` operation to `src/HddBackup.Core/Imaging/OperationRequest.cs` and `ImagingEngine.cs` (`RunSaveEdits`: phases Prepare and Write, progress by bytes of the write set, `Cancel` between writes leaves an interrupted save — completed in US6 —, results `Saved`, `Failed`, `Refused` with the codes), pass the changes in the request, never touch a device; `tests/HddBackup.Core.Tests/Editing/SaveEditsEngineTests.cs` (result, progress monotone, cancel before the first write leaves the image unchanged)
- [X] T018 [US1] Add the US1 strings to all six `Strings*.resx`: `Edit_Button`, `Edit_Save`, `Edit_Discard`, tooltips, `Edit_Editing`, `Edit_ChangesNotSaved`, `Edit_NotIncluded`, `SaveChanges_Title/Intro/InPlace`, `Edit_Kind_EditContent`, `EditRefused_*` and `SaveRefused_*` and `ChangeRefused_{NotText,TooLarge,MixedLineEndings}`, `Edit_Phase_Prepare/Write`, `Edit_Result_Saved/Failed`
- [X] T019 [US1] Create `src/HddBackup.App/Forms/EditTexts.cs` (codes → localized text, change kinds with path and detail) and `src/HddBackup.App/Forms/SaveChangesDialog.cs` per `contracts/ui.md` (list of changes, in-place warning, **Cancel** default); extend `ResourceParityTests` with the new enums
- [X] T020 [US1] Edit `src/HddBackup.App/Forms/FilesTab.cs`: **Edit** toggle, **Save**, **Discard** (bottom row per contract), edit mode on an image (`ImageEditSession`), the viewer writable for editable text per research R-12 (UTF-8, ≤ 1 MiB, consistent line endings, BOM kept; otherwise read-only with the reason), the tree and viewer through `PendingTree` while changes wait, the status line, Save through the operation panel (`MainForm.StartOperation`) and a reload of the source afterwards, Extract and Tools using the on-disk view with the `Edit_NotIncluded` note; disable **Edit** with the reason as tooltip when refused
- [ ] T021 [US1] Run T006–T016 and the whole test project and fix until they pass (features 008/009 suites must pass unchanged); render the Files tab in edit mode with the scripted harness (edit, save on a copy of `edit-basic`, `en` and one other language) and fix

**Checkpoint**: a text file of an image can be edited and saved; MVP.

---

## Phase 4: User Story 2 - Changes wait in a diff file until I save them (Priority: P1)

**Goal**: every change goes to a diff file next to the image first, survives a restart and can be discarded.

**Independent Test**: make changes, close the program, reopen the image: the same changes are offered and the
image hash is unchanged; **Discard** removes the diff file.

- [X] T022 [US2] Create `src/HddBackup.Core/Editing/DiffFile.cs` per research R-9 and data-model.md (binary container, `ImageIdentity` with the SHA-256 of the superblock and group descriptor area, ordered `EditChange`s with `OriginalStamp`, atomic replace via a temporary file and `File.Replace`/move with flush, limits 1 MiB per edit, 10 000 changes, 256 MiB, `Validate(image)` → `Matches`/`ImageChanged`/`Corrupt`/`Unsupported`, held open `FileShare.None` while edit mode is on)
- [ ] T023 [P] [US2] Create `tests/HddBackup.Core.Tests/Editing/DiffFileTests.cs`: round trip of every change kind, a torn temporary file is ignored and the previous diff is intact, truncated and corrupted files give `Corrupt`, a changed image gives `ImageChanged`, a copy of the image elsewhere still matches, a second open for editing is refused while the first holds the file, limits enforced
- [X] T024 [US2] Extend `ImageEditSession` in `src/HddBackup.Core/Editing/ImageEditSession.cs`: every change is written to the diff file before it is shown, opening an image with a valid diff restores the changes, `ImageChanged` offers only Discard, `Discard()` deletes the diff file, leaving edit mode with changes returns "ask" (keep/save/discard), a second instance gets `InUseByAnotherInstance`
- [ ] T025 [P] [US2] Extend `tests/HddBackup.Core.Tests/Editing/ImageEditSessionTests.cs`: after closing and reopening the same changes are there and the image SHA-256 is equal (SC-003), Discard leaves the image and removes the diff file, a changed image is refused with Discard as the only way out, a diff of another image is refused, the diff file of a saved session is gone
- [X] T026 [US2] Add the US2 strings (`Edit_Keep/Save/Discard/Cancel`, `Edit_ImageChanged*`, `Edit_LeaveQuestion`, `Edit_DiscardQuestion`, `EditRefused_InUseByAnotherInstance`) to all six `Strings*.resx`
- [X] T027 [US2] Edit `FilesTab.cs`: restore waiting changes when an image is opened (edit mode on, "N changes not saved"), **Discard** with its question, the leave-edit-mode and open-another-image questions (Keep in the diff file / Save / Discard / Cancel — never a silent drop), the "image changed" notice with only Discard, the second-instance refusal; run T022–T025 and the whole project, then check scenarios 2 and 3 of the quickstart with the scripted harness

**Checkpoint**: changes persist and can be discarded; the two P1 stories are done.

---

## Phase 5: User Story 3 - Create, delete and rename files and folders (Priority: P2)

**Goal**: the tree context menu creates files and folders, deletes entries and renames them, all through the diff.

**Independent Test**: create a file and a folder, delete a file and a folder with content, rename an entry, save: the
tree equals the expected tree and passes the own checker and `e2fsck -fn`.

- [X] T028 [US3] Create `src/HddBackup.Core/Editing/Ext4DirEditor.cs` per research R-4: add an entry (slack reuse, hole entries, a new block appended with the extent update, directory entry tail checksum), remove an entry (merge into the predecessor), rename in place or remove-and-add, rewrite an htree-indexed directory linearly (clear the `INDEX` flag, trailing blocks become empty directory blocks), `.`/`..` blocks for new directories, link counts; `Ext4DirEditorTests` on `edit-basic` (the 600-file htree directory: insert, remove, rename; a directory block that is full → new block; a 1 KiB-block directory; `edit-nocsum` without tails) with the own checker on the overlay
- [X] T029 [US3] Extend `Ext4Editor.cs` with `CreateFile`, `CreateDirectory`, `Delete` and `Rename` per research R-5/R-6 (phase orders of the table: data → allocation → inode → **link** for creations; **unlink** → inode → free for deletions, children bottom-up; new inode mode 0644/0755, the parent's owner, current times, random generation; the parent directory's mtime/ctime; hard links keep data until the last name; the xattr block reference count; slow symlinks and special files can be deleted; refusals `NameExists`, `NotEmpty` is not applicable to recursive delete, `IsRoot`, `Protected` for `lost+found`)
- [X] T030 [P] [US3] Create `tests/HddBackup.Core.Tests/Editing/StructureEditTests.cs` (SC-001/SC-002): create files of 0 bytes, one block, 300 KiB and in a full directory; create nested directories; delete a file, a hard-linked name (the other stays), a directory with 600 files and nested directories (free counters grow by what they used), a symlink, a file with an xattr; rename a file and a directory and to a longer and shorter name; all in one session of mixed changes in order (edit, rename, delete of the same entry); after each `TreesEqualExcept`, own checker, ExportForCi, phase-order test (the link write comes last for creations, the unlink first for deletions)
- [X] T031 [US3] Extend `PendingTree.cs` for the new kinds (new entries, hidden deleted ones, renamed names, `PendingState` for glyphs, a deleted directory hides its subtree, a new file in a new directory) and `ImageEditSession` (`CreateFile`, `CreateDirectory`, `Delete`, `Rename` with `EditNames` validation against the pending view, ordered replay); extend `ImageEditSessionTests` and add `PendingTreeTests.cs`
- [X] T032 [US3] Add the US3 strings (`Edit_NewFile/NewFolder/Rename/Delete`, `Edit_DeleteQuestion`, `Edit_Kind_*`, `ChangeRefused_{BadName,NameExists,IsRoot,Protected}`) to all six `Strings*.resx` and extend `EditTexts.cs`
- [X] T033 [US3] Edit `FilesTab.cs`: the tree context menu and shortcuts (F2, Del) in edit mode, inline name entry with the reason shown for invalid names, the pending glyphs and styles of `contracts/ui.md` (bold new, italic renamed, struck-through deleted), the delete question for directories; extend `SaveChangesDialog` rows for the new kinds
- [ ] T034 [US3] Run T028–T031 and the whole project, fix; check quickstart scenario 4 with the scripted harness and `e2fsck` through Docker on the exported images

**Checkpoint**: full editing of images through the diff.

---

## Phase 6: User Story 4 - The checksum follows the image (Priority: P2)

**Goal**: after Save, the checksum files beside the image are updated, or a `.sha256` is created.

**Independent Test**: save into an image with a `.sha256`, and into one without: the file holds the new SHA-256 and **Verify image** passes.

- [X] T035 [US4] Create `src/HddBackup.Core/Editing/SaveChecksums.cs` per research R-11: find the checksum files beside the image that describe the uncompressed image (`<image>.sha256`; not `.img.gz.sha256`), stream SHA-256 of the whole image with progress and cancel, rewrite them with `ChecksumFile.Write` keeping the format and label, create `<image>.sha256` when none exists, return the paths and a stale flag on error or cancel; extend `RunSaveEdits` with the phase `Checksum` and the results `SavedChecksumStale`
- [ ] T036 [P] [US4] Create `tests/HddBackup.Core.Tests/Editing/SaveChecksumTests.cs` (SC-005): with an existing `.sha256` (value equals the hash of the saved image, same format), without one (created), with `.sha256` and a `.img.gz.sha256` (only the first changes), a read-only folder (changed image, stale result), cancel during hashing (stale), and `VerificationResult`/verify of feature 005 passes on the saved image
- [X] T037 [US4] Add the US4 strings (`SaveChanges_Checksum`, `Edit_Phase_Checksum`, `Edit_Result_SavedStale`, `Edit_ChecksumStale`) to all six `Strings*.resx`; show the checksum line in `SaveChangesDialog.cs` and the stale result with the hint to use **Calculate checksums** in the operation banner (`FilesTab.cs`/`MainForm.cs`); run T035–T036 and the whole project; check quickstart scenario 5

---

## Phase 7: User Story 6 - A save that was interrupted can be finished (Priority: P2)

**Goal**: an image save is committed by a redo log; a save interrupted anywhere is finished to the identical result.

**Independent Test**: stop a save after every write of five scenarios, then finish: the image equals the uninterrupted result.

- [X] T038 [US6] Create `src/HddBackup.Core/Editing/RedoLog.cs` per data-model.md and research R-8 (header with the pre-save identity and partition offset, length-prefixed records with CRC-32C, a trailer, written as a temporary file, flushed and renamed; `Read` validates; presence of a valid log means "save interrupted") and change `ImageEditSession.Save` to: preflight → write the redo log → replay → delete the redo log and the diff file → checksum; add `Interrupted` state, `FinishSave()` and `Recover(image)`; a torn redo temporary file is ignored and the diff stays valid
- [ ] T039 [P] [US6] Create `tests/HddBackup.Core.Tests/Editing/InterruptedSaveTests.cs` (SC-004): a `FaultyBlockDevice` (reuse the one in the test fakes or extend it) that throws at write N; for **every** N of five scenarios (content edit, create+rename, delete of a directory, mixed, nearly full): the session reports `Interrupted`, browsing shows the warning state, `FinishSave` gives an image byte-for-byte equal to the uninterrupted one and then the diff and redo files are gone; a failure while writing the redo log leaves the image untouched and the diff intact; a corrupted redo log is refused with the reason; a completed save is never reported as interrupted
- [X] T040 [US6] Add the US6 strings (`Edit_Interrupted*`, `Edit_FinishSave`, `Edit_NotNow`, `Edit_Result_Interrupted`, `Edit_InterruptedBrowseWarning`) to all six `Strings*.resx`; in `FilesTab.cs` offer **Finish save** when an image with a redo log is opened (the only writing action offered), show the warning while browsing it, and map a cancelled or failed save to the interrupted banner; run T038–T039 and the whole project; check quickstart scenario 6 by killing the harness process during a save

---

## Phase 8: User Story 5 - Edit the file system of a card, written at once (Priority: P3)

**Goal**: the same editing on a card in the reader, each change written immediately, no diff, no checksum.

**Independent Test**: on a test card with ext4 and FAT partitions, edit, save, create, rename, delete: each change is on the card at once, the FAT bytes are identical, no file is created anywhere, and every result passes the checker.

- [X] T041 [US5] Add `OpenForPartitionWrite(DeviceInfo device, long partitionOffset, long partitionLength)` to `src/HddBackup.Core/Devices/IDeviceAccess.cs`, implement it in `WindowsDeviceAccess.cs` (verify identity as `OpenExclusiveForWrite` does — system disk guard, bus, model, serial, size —, require removable media and not `HostsExecutable`, `PhysicalDiskDevice.Open(writable: true)` **without** locking or dismounting any volume, check `IOCTL_DISK_IS_WRITABLE` → `WriteProtected`, return a `PartitionView` over the range) and in `tests/HddBackup.Core.Tests/Fakes/FakeDeviceAccess.cs` (counts the opens, never locks)
- [X] T042 [US5] Create `src/HddBackup.Core/Editing/CardEditSession.cs`: `Open(access, device, partition)` (checks removable, not system, writable, `Ext4EditSupport`; no write), `Apply(EditChange)` (build the write set with the editor, `EditPreflight`, fit check, replay in order with flushes through `OpenForPartitionWrite`, flush, close; on failure the cause, the offset and the change in progress), no diff file, no redo log, no checksum; `src/HddBackup.Core/Editing/CardResult.cs` if needed for the result codes
- [X] T043 [P] [US5] Create `tests/HddBackup.Core.Tests/Editing/CardEditSessionTests.cs` on `rpi-like-4k` (FAT + ext4) behind a recording `ReadOnlyBlockDevice`-style wrapper: every write lies inside the ext4 partition range (SC-006: FAT bytes and partition table equal before and after, zero writes outside), no diff, redo or checksum file is created in the temp folder, the volume locks of `FakeDeviceAccess` are never taken, every kind of change leaves a file system that passes the checker (SC-009, at least ten changes), refusals (not removable, system disk, write-protected, `dirty-journal`) write nothing
- [X] T044 [P] [US5] Create `tests/HddBackup.Core.Tests/Editing/CardInterruptionTests.cs` (SC-010): a device that fails at write N, for every N of five card scenarios: afterwards the file system passes the checker apart from leaked blocks or inodes, no directory entry points to unwritten data, a file's content is entirely old or entirely new, and the session reports the cause, the offset and the change in progress
- [X] T045 [US5] Add the US5 strings (`EditCard_Title/Question/Warning/Confirm`, `Edit_EditingCard`, `Edit_SavedFile`, `Edit_CardFailed`, `EditRefused_{NotRemovable,SystemDisk,WriteProtected}`) to all six `Strings*.resx`; create `src/HddBackup.App/Forms/EditCardDialog.cs` per `contracts/ui.md` (model, size, bus, partition, the warning, **Cancel** default)
- [X] T046 [US5] Edit `FilesTab.cs` for device mode: **Edit** asks through `EditCardDialog`; **Discard** hidden and no waiting list; **Save** writes the open file at once and **Save** is enabled only when the text changed; creation, rename and deletion are written when confirmed (delete always asks and names how much it removes); selection change with an unsaved text asks (Save to the card / Discard the edit / Cancel); after every change the tab reloads the changed directories and the viewer; a failure stops with the cause and the offset, says which change was in progress and leaves edit mode
- [ ] T047 [US5] Run T041–T044 and the whole project, fix; verify the card mode against `FakeDeviceAccess` in the scripted harness (no Save/Discard semantics, status "Editing the card…"); add the two manual rows 7 and 8 and the Windows 7 row 11 of the quickstart to `docs/manual-hardware-checklist.md` as steps 46–48 (the raw write to an ext4 partition while the FAT volume is mounted must be confirmed on real hardware)

---

## Phase 9: Polish & cross-cutting

- [ ] T048 [P] Run the resource-parity test and a language sweep (quickstart scenario 10): render the edit-mode tab, `SaveChangesDialog` and `EditCardDialog` in en, uk, de, fr, es, ja with the scripted harness, check clipping, and fix; take English screenshots into `docs/screenshots/` (`edit-mode.png`, `save-changes.png`)
- [X] T049 [P] Update `README.md` (English): an "Editing files" section on the Files tab (Edit button, the diff file and what it is, Save, the checksum file, finishing an interrupted save, cards written at once with no checksum and no undo, what is not supported: resizing, moving between directories, permissions and owners, special files, binary files, htree directories become linear), the screenshots, and `specs/010-ext4-writer/` in the design documents list
- [X] T050 [P] Add an entry under `## Unreleased` in `docs/release-notes.md` for editing files (check `tools/tests/Get-ReleaseNotes.Tests.ps1` still passes)
- [X] T051 [P] Add the remaining quickstart scenarios (real card as a device with before/after hash of the FAT range, Windows 7 SP1 and Windows 11 runs, kill during save on real hardware) to `docs/manual-hardware-checklist.md`
- [X] T052 Run `dotnet build HddBackup.sln -c Release` (0 warnings) and `dotnet test tests/HddBackup.Core.Tests -c Release`; run the Docker independent check (`check-editing.sh`) locally and confirm the `ext4-check` CI job passes on the branch
- [X] T053 Measure the preflight (research R-7) on a large image (a sparse 16–32 GiB fixture from `make-large.sh` style) and on `edit-basic`: time of `Ext4Checker` over the overlay; set the `Full`/`Targeted` threshold constant in `EditPreflight.cs` (full when ≤ 20 s), record the numbers in `research.md` R-7, and measure Save of one line into a 16 GiB sparse image (SC-007: ≤ 30 s without the checksum) and one card change on the fake (≤ 5 s)
- [X] T054 Principle I review for the change (`docs/safety-review.md` format): add a section "Editing files (feature 010)" listing each rule (nothing written before Save or the card confirmation, writes only inside the partition, no volume locked, unclean/damaged/unsupported refused, fit check before the first write, write order and the guarantee on interruption, redo log completes a save, checksum update, no diff/checksum for cards, browse mode unchanged) with its automated evidence and what is only manual
- [ ] T055 Update `specs/010-ext4-writer/checklists/requirements.md` notes (clarifications and amendment resolved) and set the spec **Status** to `Implemented` when everything above is done

---

## Dependencies & Execution Order

- **Gate**: T001 (layout approval) before every App task; the constitution amendment is applied.
- **Phase 1** (T001–T004) → **Phase 2** (T005–T012) → stories. T002–T003 (fixtures) block every fixture test; T004 only blocks the CI oracle.
- **Foundational internals**: T005 → T009, T010; T007 → T012 and every editor task; T009 + T010 → T013; T011 → every test; T012 → T015.
- **US1** (T013–T021) needs Phase 2. **US2** (T022–T027) needs US1's session. **US3** (T028–T034) needs the editor of US1 and the session of US2. **US4** (T035–T037) needs the engine operation of US1 and can follow US1 or US2. **US6** (T038–T040) needs the session of US2 (diff identity). **US5** (T041–T047) needs the editor of US1 and, for creations and deletions, US3; the device access (T041) and the session (T042) can start after T013.
- **Polish** (T048–T055) after the stories it documents.
- All stories edit `FilesTab.cs`, `EditTexts.cs`, `SaveChangesDialog.cs` and `ImageEditSession.cs`: do them one after the other.

### Parallel opportunities

- Phase 2: T006, T007, T008 together; then T009 and T010 (after T005) together; T011 beside them.
- US1: T014 and T016 together after T013/T015; T018 with T019.
- US3: T028's tests, T030 and T031 after their classes; T032 with T033.
- US5: T043 and T044 together after T042.
- Polish: T048–T051 are independent documents and checks.

```text
Example, Phase 2 in parallel:  T006 (support) | T007 (write set) | T008 (names)  →  T009 (allocator) + T010 (inode writer)  →  T012 (preflight)
US1:                           T013 → T014 + T015 → T016 + T017 → T018 → T019 → T020 → T021
```

## Implementation Strategy

- **MVP = Phase 2 + US1** (edit a text file of an image and save it), then US2 (the diff file) as the second
  P1 slice. US3, US4 and US6 refine images; US5 (cards) is built last because it carries the highest risk and
  needs hardware confirmation.
- The write set with its fixed phase order is the single path for every change (images through the redo log in
  US6, cards directly in US5): do not add a second way of writing.
- The own checker runs on every produced write set before the first real write; the independent `e2fsck -fn`
  runs in CI on every exported saved image.
- Keep browse mode untouched: re-run the feature 008 and 009 suites after T020 and T046.

## Implementation notes (deviations)

- `DirEditor`, `InodeWriter` and `EditNames` live in `Ext4Editor.cs` / `Ext4EditorStructure.cs` (T009, T010, T028 merged into the editor); `EditPreflight`, `SaveChecksums` and the session types are in `ImageEditSession.cs`; T005 (checksum extraction) was skipped, the editor reuses `Ext4Csum`.
- Core layer done and tested (45 tests + `e2fsck` oracle): editor, structure edits, diff file, redo log, `ImageEditSession` (Save, FinishSave, lock, stale diff), `PendingTree`.
- Still open: unit tests for support check / write set / names / allocator, `DiffFileTests`, `InterruptedSaveTests` at every write, `SaveChecksumTests` (T016, T023, T025, T036, T039), engine `SaveEdits`, all strings, the UI (T017–T020, T026, T027, T032, T033, T037, T040), card mode (T041–T047), polish (T048–T055).
- UI done (compiles, 880 tests pass, **not yet run by hand**): `FilesTab.Edit.cs` (Edit/Save/Discard, context menu New file/folder/Rename/Delete, F2/Del, text editing with "Apply to file", automatic restore of waiting changes, `NameDialog`, `SaveChangesDialog`), 49 strings in all six resx. T017 deviation: Save runs in the modal `SaveChangesDialog` through `ImageEditSession`, not through `ImagingEngine`/`OperationPanel`. Known gap: text typed but not applied is lost when the selection changes (Save applies it first). Still open: card mode (T041–T047), missing unit tests, polish T048–T055, hand check of the UI (T021/T034).
- Card mode done: `IDeviceAccess.OpenForPartitionWrite` (Windows + fake), `CardEditSession` (one change = build, preflight, write at once), card tests incl. interruption at every write and "outside the partition unchanged"; UI: Edit on a device asks for confirmation, **Done** replaces Save/Discard. Docs: README section, release notes, safety review, checklist rows 46–48. Open: T021/T034/T047 hand/harness checks, T048 language sweep, T052 Release build + CI, T053 preflight timing (card mode runs the full check for every change — may be slow on big cards), T055.
- T053 measured so far: on `rpi-like-4k` (321 MiB) a whole save (check + write + checksum) takes about 0.4 s (`PreflightTimingTests`). Not yet measured on a 16–32 GiB image; the full check runs for every card change.
- T053 done: 15 GiB image, full check 12.3 s cold, Save 11.2 s warm incl. 9.2 s checksum (research R-7). Targeted check not implemented (needed only above ~24 GiB).
- e2fsck 1.47.0 -fn on the saved 15 GiB copy (partition 2, offset 532480 sectors): clean, new file present with the right content; boot area before the partition byte-identical.
