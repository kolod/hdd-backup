---

description: "Task list for feature 008: browse and extract files from an ext4 partition"
---

# Tasks: Browse and Extract Files from an ext4 Partition

**Input**: Design documents from `/specs/008-ext4-browser/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md

**Tests**: Included — constitution Principle VI requires test-verified disk I/O, every safety rule
of Principle I needs a test, and the success criteria (SC-003, SC-004, SC-008) are defined against
fixtures and an independent Linux check.

**Organization**: US1 browse the tree (P1), US2 extract (P1), US3 view file content (P2), US4 browse
a physical device (P2), US5 damaged / not cleanly unmounted file systems (P3).

## Format: `[ID] [P?] [Story] Description`

- Paths: `src/HddBackup.Core/`, `src/HddBackup.App/`, `tests/HddBackup.Core.Tests/`,
  `tests/fixtures/ext4/`, `tools/ci/`
- Strings: every new key goes into all six `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx`
  (key list and English text in `contracts/ui.md`); the existing resource-parity test must pass.
- Tests for Core logic use the file-backed and in-memory devices and fixtures already in
  `tests/HddBackup.Core.Tests/`; fixtures are copied to the output by the existing
  `Fixtures\ext4\*.*` rule.
- Core public types expose no `Ext4*` internal types (research R-1).

---

## Phase 1: Setup — gates and fixtures

- [X] T001 Get the draft layout mockup in `specs/008-ext4-browser/spec.md` approved by the maintainer; change its heading to `(approved)`, the **Status** line to `Draft (layout approved <date>)` and add the approval to the Clarifications. **Gate: no UI task (T017–T019, T034–T037, T042, T049) starts before this.**
- [X] T002 Apply the constitution amendment 1.3.0 drafted in `specs/008-ext4-browser/research.md` R-8 to `.specify/memory/constitution.md`: Principle VII scope text, a new Sync Impact Report block at the top (1.2.0 → 1.3.0, MINOR, rationale, modified sections, "Templates requiring updates: none"), the "File systems" line of Technology & Platform Constraints (add "browsed" and "read-only extraction"), and the Version line (`1.3.0`, Last Amended = today). **Gate: needs the maintainer's confirmation of the R-8 wording; no implementation task starts before this.** Update the plan's Constitution Check row VII to ✅ afterwards.
- [X] T003 Create `tests/fixtures/ext4/make-browse-fixtures.sh` (model: `make-repair-fixtures.sh`, helpers in `fixture-lib.sh`) building `browse-names.img.gz` (single ext4 partition image, 4 KiB blocks: ordinary tree with `/etc/hostname` text and a binary `/bin/ls`-like file; names with spaces, UTF-8 (`Ünï cödé.txt`, `日本語.txt`), an invalid-UTF-8 name (bytes `\xff\xfe`), names Windows cannot store (`a:b`, `what?.txt`, `trailing.`, `CON`, `nul.txt`), two names differing only by case (`Readme`, `README`), a 300-character path of nested directories, fast and long symlinks, a FIFO, a socket, hard links, an empty file) and `browse-large.img.gz` (a sparse file with a 2 GiB hole and 4 KiB of data at the end, a regular 5 MiB file, and a directory with 20 000 empty files); each with `<name>.manifest.tsv` in the existing manifest format (see `ManifestEntry` in `tests/HddBackup.Core.Tests/Fixtures/FixtureImages.cs`); output to `tests/HddBackup.Core.Tests/Fixtures/ext4/`
- [X] T004 Run `make-browse-fixtures.sh` in Docker (`debian:bookworm`, command in `quickstart.md`), check the fixtures into the repository (`.gitattributes` already marks `*.img.gz` binary and `*.tsv` -text) and confirm `FixtureImages.RawPath("browse-names")` and `FixtureImages.Manifest("browse-names")` work without code changes (the helper takes any name)
- [X] T005 Extend `tools/ci/Invoke-Ext4Checks.sh` (and `tests/fixtures/ext4/check-output.sh` where shared) with a loop over `$EXPORTS/extracted/<fixture>.tsv` listings written by the extraction tests (T031; a listing of directories and file SHA-256, not the files): for each, run `debugfs -R "rdump / <tmpdir>"` on the matching fixture image and compare directories and regular files (`find -type d`, `find -type f` with `sha256sum`) between the two trees, skipping symbolic links, FIFOs, sockets and device files; exit non-zero on any difference; the loop is a no-op when `extracted/` does not exist

---

## Phase 2: Foundational — shared state probe, DTOs, read-only fake

- [X] T006 [P] Create `src/HddBackup.Core/Ext4/Ext4StateProbe.cs`: `internal static class Ext4StateProbe` with `RepairReasons ReasonsOf(Ext4Superblock sb)` and `FileSystemState Probe(Ext4Superblock sb, int partitionNumber, string name, bool isRoot)` — move the logic of `RepairReasonsOf` and the per-partition body of `AddFileSystemStates` out of `src/HddBackup.Core/Shrinking/ShrinkPlanner.cs` unchanged (Clean / NeedsRepair with reasons / Damaged for recorded errors); make `ShrinkPlanner` call it; no behavior change (research R-3)
- [X] T007 [P] Create `tests/HddBackup.Core.Tests/Ext4/Ext4StateProbeTests.cs`: clean fixture → `Clean`; `not-clean`, `dirty-journal`, `orphans` fixtures → `NeedsRepair` with the right reasons; a superblock with the error flag or a non-zero error count → `Damaged`; the existing `Shrinking` tests still pass unchanged
- [X] T008 [P] Create `src/HddBackup.Core/Browsing/FsEntry.cs` per data-model.md: `public sealed class FsEntry` (`Inode`, `Name`, `Path`, `Kind`, `Size`, `Mode`, `Uid`, `Gid`, `Modified`, `LinkTarget`, `Unreadable`, `NotViewable`), `public enum FsEntryKind { Directory, File, Symlink, Special }`, `public enum SpecialKind { None, CharDevice, BlockDevice, Fifo, Socket }`, `public sealed class FileSystemCounts { Files, Directories, Approximate }`, and `public static string FormatMode(ushort mode)` returning `-rw-r--r--` style text
- [X] T009 [P] Create `src/HddBackup.Core/Browsing/BrowsePartition.cs`: `public sealed class BrowsePartition` (`Number`, `Name`, `Offset`, `Length`, `FileSystem`, `IsBrowsable`, `Unsupported` reason text, `State` of type `FileSystemState`) and `public enum BrowseProblem { None, NeedsConversion, NoExt4, Unreadable }`
- [X] T010 [P] Create `tests/HddBackup.Core.Tests/Fakes/ReadOnlyBlockDevice.cs`: `IBlockDevice` decorator over any inner device that counts and rejects `Write`/`Flush` (throws `InvalidOperationException`, increments `WriteCalls`), reports `CanWrite == false`; used by all Browsing tests to prove zero writes (research R-9)

**Checkpoint**: the shared probe builds and shrink tests still pass; DTOs and the fake compile.

---

## Phase 3: User Story 1 - Browse the file system of an image (Priority: P1) 🎯 MVP

**Goal**: choose an image, pick the ext4 partition, see and expand the tree, see the counts.

**Independent Test**: open `browse-names` and `browse-large`; compare names, kinds and sizes in the
tree with the manifests; `.img.gz` is refused with the conversion hint.

- [X] T011 [US1] Create `src/HddBackup.Core/Browsing/BrowseSource.cs`: `public sealed class BrowseSource : IDisposable` with `static BrowseSource OpenImage(string path)` — `.img.gz` (by `ImageDescriptor`/extension) returns a source with `Problem = NeedsConversion` and no handles; otherwise `FileBlockDevice.OpenRead`, `PartitionTableReader.Read` (single-partition images via `ImageDescriptor.Kind == ImageKind.Partition` or a probe at offset 0 → one pseudo-partition), `BrowsePartition` for every partition with `IsBrowsable` = ext4 + valid magic + `Ext4Features.Unsupported(sb)` empty + supported block size, `Unsupported` carrying the feature name or reason, `State` from `Ext4StateProbe`; `Problem = NoExt4` when none is browsable; `Ext4FileSystemView OpenFileSystem(BrowsePartition p)` creating a `PartitionView` + `Ext4Reader`; read errors surface as `ImagingException`; disposing closes everything. Source is read-only by construction (no write path in this class)
- [X] T012 [US1] Create `src/HddBackup.Core/Browsing/Ext4FileSystemView.cs`: `public sealed class Ext4FileSystemView : IDisposable` per data-model.md — `Root`, `GetChildren(FsEntry dir, CancellationToken ct)` (reads `ReadDirectory`, kind from the dirent file-type byte or the inode, sorted directories first then ordinal by decoded name, UTF-8 decoding with U+FFFD, symlink targets via `ReadSymlink`, per-entry `try/catch` of `Ext4FormatException`/`ImagingException` marking `Unreadable` with the message), `GetEntry(string path)`, `Counts` from group descriptors (directories = sum of `UsedDirs`; files = `InodesCount − FreeInodesCount` − directories − inodes below `FirstInode`; `Approximate` when the state is `Damaged`), `State`, and one internal lock serializing all reader access (research R-4); no `Ext4*` type in the public surface
- [X] T013 [P] [US1] Create `tests/HddBackup.Core.Tests/Browsing/BrowseSourceTests.cs`: whole-device image with several partitions lists only the ext4 ones as browsable and the rest as not browsable with a reason; single-partition image opens directly; one ext4 partition is the only candidate; image without ext4 → `Problem = NoExt4` with the partitions present; `.img.gz` → `NeedsConversion` and no file created beside it; unsupported feature (synthetic superblock with `meta_bg`) → `Unsupported` names it; everything runs over `ReadOnlyBlockDevice` where a device is injectable, and the image file hash is unchanged afterwards
- [X] T014 [P] [US1] Create `tests/HddBackup.Core.Tests/Browsing/FileSystemViewTests.cs`: for `browse-names`, walk the whole tree through `GetChildren` and compare every path, kind, size, mode, uid/gid, mtime and link target with the manifest (`ManifestAssert` helpers); sorting rule (directories first, ordinal); invalid-UTF-8 name shows U+FFFD; hard links appear under both names; `Counts` equal the manifest's file and directory counts; `browse-large`: the 20 000-entry directory is returned in a single call within the SC-002 budget (assert under 2 s on the test machine, generous bound), and the sparse 2 GiB file reports its size without reading it
- [X] T015 [US1] Run T013–T014 and fix T011–T012 until they pass; run the full `Shrinking` tests to confirm T006 changed nothing
- [X] T016 [US1] Add the US1 strings to all six `Strings*.resx`: `Tab_Files`, `Files_Source`, `Files_SourceImage`, `Files_Partition`, `Files_StateClean`, `Files_StateNeedsRepair`, `Files_StateDamaged`, `Files_NeedsConversion`, `Files_NoExt4`, `Files_Unsupported`, `Files_Counts`, `Files_CountsApprox`, `Files_Opening`, `Files_ReadError`, `Files_Unreadable`, `Files_LinkTarget`, `Files_SpecialFile`, `Tooltip_Files_State` (English text in `contracts/ui.md`; translate for uk, de, fr, es, ja)
- [X] T017 [US1] Create small tree icons in code (folder, file, link, special, unreadable) as an `ImageList` of 16×16 bitmaps drawn with GDI+ in `src/HddBackup.App/Forms/FilesTreeIcons.cs` (no embedded binary resources, no new dependency; DPI-aware sizes via the existing `Ui` helpers in `src/HddBackup.App/Forms/Ui.cs`)
- [X] T018 [US1] Create `src/HddBackup.App/Forms/FilesTab.cs` (UserControl, constructed with the shared `SelectedImage` like `ImageTab`): layout per `contracts/ui.md` (source row with image path box and **Browse…**, partition drop-down with state text, `SplitContainer` with the `TreeView` on the left and an empty right panel reserved for US3, status line at the bottom); open the image on a worker thread with a cancellation token (status `Files_Opening`), fill partitions, auto-select a single one, open the file system, show `/` and counts; directories expand lazily (placeholder child filled in `BeforeExpand` on a worker thread, `BeginUpdate`/`EndUpdate`); `.img.gz` and no-ext4 states per the state table; changes of `SelectedImage.Changed` reopen; dispose the view and source on change and on close; all texts via `LanguageService` with `ApplyStrings()` like the other tabs
- [X] T019 [US1] Edit `src/HddBackup.App/Forms/MainForm.cs`: add `_filesPage` and `FilesTab` as the fourth tab after Image (`Tab_Files`), include it in the language switch / `ApplyStrings()` path and in the "operation running" enable/disable path used by the other tabs
- [X] T020 [US1] Manual check of quickstart scenarios 1 and 15 (root within 5 s, counts, conversion hint) and fix; take the layout screenshot of the new tab at 100% in English

**Checkpoint**: the tree of an image can be browsed; MVP of the read-only side.

---

## Phase 4: User Story 2 - Extract a file or a directory (Priority: P1)

**Goal**: **Extract…** copies the selected file or directory to a Windows folder with progress,
cancellation, conflict handling and a summary.

**Independent Test**: extract files and directory trees from `browse-names` and the existing
fixtures; every regular file equals the manifest SHA-256; cancel leaves nothing that looks complete.

- [X] T021 [P] [US2] Create `src/HddBackup.Core/Browsing/WindowsNames.cs`: `internal static class WindowsNames` with `string Map(string decodedName, ISet<string> usedLowercase, out bool renamed)` implementing research R-6 (invalid characters `< > : " / \ | ? *`, control characters and U+FFFD → `_`; trailing dot or space → `_`; reserved device names with or without extension get a leading `_`; case-insensitive collisions get ` (2)`, ` (3)` before the extension; deterministic for entries processed in order) and `string ToExtendedPath(string path)` adding `\\?\` / `\\?\UNC\`
- [X] T022 [P] [US2] Create `tests/HddBackup.Core.Tests/Browsing/WindowsNamesTests.cs`: table-driven cases for every rule (reserved names `CON`, `con.txt`, `COM1`, `LPT9.log`; `a:b`, `what?.txt`, `trailing.`, `trailing `; `Readme`/`README` pair → second becomes `README (2)`; U+FFFD; an already used `(2)` name; names that are fine stay unchanged and report `renamed == false`); extended-path conversion for drive and UNC paths, already-prefixed paths unchanged
- [X] T023 [US2] Create `src/HddBackup.Core/Browsing/ExtractionPlanner.cs` with the public types `ExtractionPlan`, `ExtractionItem`, `ExtractionIssue`, `ExtractionIssueKind`, `ConflictPolicy` per data-model.md and `ExtractionPlan Plan(Ext4FileSystemView view, FsEntry root, string destinationFolder, PathInfo destinationInfo, ConflictPolicy policy, CancellationToken ct)`: walk the selection depth-first in sorted order; map names with `WindowsNames`; classify items (`Copy`, `CreateDirectory`, `SkipSymlink`, `SkipSpecial`, `Fail` for unreadable / not viewable entries, over-long paths beyond 32 767 characters, and files at or above `DestinationChecks.Fat32MaxFileSize` + 1 on a FAT/FAT32 destination); count existing destination files that would be replaced (`Conflicts`) and mark them `SkipExisting` under policy `Skip`; total bytes of files to copy; free-space check against `destinationInfo.FreeBytes` (when known) producing a typed `Insufficient` result with both sizes; hard-linked inodes are copied once per name; no I/O on file contents
- [X] T024 [P] [US2] Create `tests/HddBackup.Core.Tests/Browsing/ExtractionPlannerTests.cs`: plan of a file and of a directory from `browse-names` (counts, bytes, renamed list, skipped symlinks/FIFO/socket, the 300-character path flagged per the limit rule, case pair renamed); conflicts counted for a pre-populated destination, `Skip` policy marks them, `Replace` does not; insufficient free space reported with both sizes; FAT32 destination with a ≥ 4 GiB file (sparse fixture file) → `Fail` item with the FAT reason; unreadable entry → `Fail` item and the rest planned; cancellation honored
- [X] T025 [US2] Create `src/HddBackup.Core/Browsing/Ext4Extractor.cs`: `internal sealed class Ext4Extractor` — `ExtractionSummary Run(Ext4FileSystemView view, ExtractionPlan plan, Action<long> bytesDone, CancellationToken ct)`: create directories first, copy each file in chunks of the pipeline buffer size (4 MiB, reading `ReadRange`, holes as zeros) to `.hddbackup-part-<id>` in the destination directory, rename over/into the final name when complete (replace only under policy `Replace`), set file mtime after the rename and directory mtimes in post-order after their contents, delete the part file on cancel or error, record each failure as an `ExtractionIssue` and continue; fail the whole run only for destination write errors that make continuing pointless (disk full) and source device errors (rethrow `ImagingException`); all paths through `WindowsNames.ToExtendedPath`; `ExtractionSummary` per data-model.md
- [X] T026 [US2] Edit `src/HddBackup.Core/Errors/ImagingException.cs`: add `ErrorCode.DestinationIsSource` (destination is the image file or a volume of the browsed device); map it to a localized message wherever `ErrorCode.ImageOnSource` is mapped in `src/HddBackup.App` (search for it) with the string `Extract_DestinationIsSource`
- [X] T027 [US2] Edit `src/HddBackup.Core/Imaging/OperationRequest.cs`: add `OperationKind.ExtractFiles` and the properties `BrowseImagePath`/`BrowseDevice` (source), `BrowsePartitionNumber`, `ExtractEntryPath`, `ExtractDestination`, `ExtractConflictPolicy`; edit `src/HddBackup.Core/Imaging/OperationResult.cs`: add `ExtractionSummary Extraction`
- [X] T028 [US2] Edit `src/HddBackup.Core/Imaging/ImagingEngine.cs`: `Validate` for `ExtractFiles` throws `InvalidRequest` for missing fields and `DestinationIsSource` when `DestinationChecks`-style tests hold (`FileIdentity.IsSameFile` for the image path against any planned destination file is checked in the extractor, here only the folder: destination folder on the browsed device's disk via `_access.GetPathInfo(destination).DiskNumbers` containing `request.BrowseDevice.DiskNumber`, or the image path inside the destination folder tree under a name that the plan would write); `RunAsync` uses `new PhaseModel(false)` for the new kind; add `RunExtract` opening its own read-only `BrowseSource`, planning (phase Preparing), applying the conflict policy, then extracting (phase Transferring with total bytes; Finalizing for directory times), filling `OperationResult.Extraction`, mapping cancel/failure like the other operations; `SafeToRemove` is always true (nothing written to media); also make the extractor refuse writing any file whose full path `FileIdentity.IsSameFile` equals the image path (Principle I, commit 859ae42 precedent)
- [X] T029 [P] [US2] Create `tests/HddBackup.Core.Tests/Browsing/ExtractorTests.cs`: extract each of `browse-names`, `rpi-like-4k` and `rpi-like-1k` fully; every regular file equals its manifest SHA-256 and mtime (seconds), directory mtimes preserved, renamed items match the expected list, symlinks/FIFO/socket not created and listed; hard links extracted as independent copies; cancel at the start, in the middle of a large file and during directory times leaves no `.hddbackup-part-*` and no partly written final file; an unreadable item (fault-injecting reader or the damaged fixture) is reported and the rest is extracted; destination disk full (a fake `PathInfo` plus a failing stream) stops with `DestinationFull`; replace policy overwrites, skip policy keeps the existing file's bytes
- [X] T030 [P] [US2] Create `tests/HddBackup.Core.Tests/Browsing/ReadOnlyTests.cs` (Principle I tests): browsing, planning and extracting over `ReadOnlyBlockDevice` observe zero `Write`/`Flush` calls; the image file's SHA-256 is identical before and after extraction; engine `Validate` refuses a destination that is the image's folder when the plan would write a file onto the image path, and a destination folder on the browsed device's disk (`FakeDeviceAccess.GetPathInfo` returning that disk number) with `DestinationIsSource`; the engine never asks for write confirmation (no `ExternalDiskConfirmed` or lock calls on the fake access)
- [X] T031 [US2] In the extraction tests (T029) export each extracted tree of `rpi-like-4k` and `rpi-like-1k` to `<HDDBACKUP_EXPORT_DIR>/extracted/<fixture>/` when the environment variable `HDDBACKUP_EXPORT_DIR` is set (same variable and locking as `Ext4WriterTests.Rebuild`), so the `ext4-check` job compares them with `debugfs rdump` (T005)
- [X] T032 [US2] Run T022, T024, T029, T030 and fix T021–T028 until they pass; run the whole test project
- [X] T033 [US2] Add the US2 strings to all six `Strings*.resx`: `Files_Extract`, `Tooltip_Files_Extract`, `Files_ExtractTitle`, `Files_Planning`, `Extract_NoSpace`, `Extract_DestinationIsSource`, `Extract_Conflicts`, `Extract_Replace`, `Extract_SkipExisting`, `Phase_Planning`, `Phase_Extracting`, `Result_Extracted`, `Result_ExtractedWithIssues`, `Result_ExtractCancelled`, `Extract_Details`, `Extract_IssueSkippedLink`, `Extract_IssueSkippedSpecial`, `Extract_IssueSkippedExisting`, `Extract_IssueRenamed`, `Extract_IssueTooLong`, `Extract_IssueFatLimit`, `Extract_IssueUnreadable`
- [X] T034 [P] [US2] Create `src/HddBackup.App/Forms/ExtractConflictDialog.cs`: modal dialog with the text `Extract_Conflicts` ({0} = count) and buttons **Replace**, **Skip existing**, **Cancel** (default Cancel), returning a `ConflictPolicy` or null; no new window chrome beyond the other dialogs (model: `ShrinkOfferDialog.cs`)
- [X] T035 [P] [US2] Create `src/HddBackup.App/Forms/ExtractSummaryDialog.cs`: modal dialog with a read-only list (kind, original path, resulting name, reason using the `Extract_Issue*` strings), the counts line, and a **Copy** button putting the list on the clipboard as text; reusable for the panel's **Details…** action
- [X] T036 [US2] Edit `src/HddBackup.App/Forms/FilesTab.cs`: **Extract…** button and tree context menu item (enabled for a readable selected entry when no operation runs); flow per `contracts/ui.md`: folder browser → planner on a worker thread (status `Files_Planning`) → refusals (`Extract_NoSpace` with both sizes, destination guards, nothing to extract) → `ExtractConflictDialog` when `Conflicts > 0` → `OperationPanel.Start` with the `ExtractFiles` request on a new `ImagingEngine`; disable source and partition while running; on completion show `Result_Extracted` / `Result_ExtractedWithIssues` / `Result_ExtractCancelled` through the panel and open `ExtractSummaryDialog` from **Details…**
- [X] T037 [US2] Edit `src/HddBackup.App/Forms/OperationPanel.cs` and `src/HddBackup.App/Forms/MainForm.cs` as needed so the shared panel shows the phases `Phase_Planning` / `Phase_Extracting` and a **Details…** action next to the result banner for `ExtractFiles`, and so the Files tab's request uses the same panel instance and cancel path as the other tabs
- [X] T038 [US2] Manual check of quickstart scenarios 6, 7, 10, 11, 12 (extract directory, conflict dialog, no space, cancel, destination guard) and fix. Done through the real MainForm with a scripted harness (scenario 6: result banner, details, files on disk); scenarios 7, 10, 11, 12 are verified at engine and extractor level by ReadOnlyTests and ExtractorTests; the modal dialogs themselves (folder picker, conflict question, message boxes) were not clicked through by hand

**Checkpoint**: files and directories can be browsed and extracted; MVP of the feature.

---

## Phase 5: User Story 3 - View the content of a file (Priority: P2)

**Goal**: selecting a file shows its beginning as text or a hex dump, with a header line.

**Independent Test**: select a text file, a binary file, an empty file and a large file in
`browse-names` / `browse-large` and compare what the viewer shows.

- [X] T039 [US3] Create `src/HddBackup.Core/Browsing/FilePreview.cs`: `public sealed class FilePreview` (`Mode`, `Text`, `ShownBytes`, `TotalBytes`, `Truncated`) and `public static FilePreview Create(Ext4FileSystemView view, FsEntry file, PreviewMode requested, CancellationToken ct)` with `PreviewMode { Auto, Text, Hex }`: read at most 1 MiB for text and 256 KiB for hex via `ReadRange`; Auto decides from the first 8 KiB (no NUL bytes and valid UTF-8, allowing a truncated final sequence → text, else hex); text decoded as UTF-8 (BOM skipped, invalid bytes as U+FFFD, `\n` normalized to `\r\n` for the control, cut at a character boundary); hex lines `00000000  48 65 6C … |Hello…|` with offsets and 16 bytes per line; empty file → empty text; non-files return no content (header only); `NotViewable` entries return their reason (research R-10)
- [X] T040 [P] [US3] Create `tests/HddBackup.Core.Tests/Browsing/FilePreviewTests.cs`: text file (ASCII and UTF-8 with `日本語`), binary file (NUL bytes) → Hex in Auto and text-with-replacement in forced Text, forced Hex of a text file, empty file, file larger than the limits → `Truncated` with `ShownBytes`/`TotalBytes` and no read beyond the limit (counted on the device fake), truncated UTF-8 tail at the limit not producing a broken character, hex line format exactly as specified, directory/symlink/special → no content, cancellation honored
- [X] T041 [US3] Add the US3 strings to all six `Strings*.resx`: `Files_ShownOf`, `Files_ViewText`, `Files_ViewAuto`, `Files_ViewHex`, `Files_NotViewable`, `Tooltip_Files_ViewHex`
- [X] T042 [US3] Edit `src/HddBackup.App/Forms/FilesTab.cs`: fill the right panel — header label (path, type, size, mode via `FsEntry.FormatMode`, uid/gid, local time; link target; unreadable / not viewable reasons), read-only multiline monospaced `TextBox` (`MaxLength` raised so 1 MiB fits; fall back to a plain-text `RichTextBox` if the measurement in T045 shows truncation), radio buttons **Text** / **Auto** / **Hex** (default Auto, re-render the same bytes on change), preview created on a worker thread and cancelled when the selection changes (only the latest renders), "Showing the first … of …" note under the viewer
- [X] T043 [US3] Run T040 and fix T039 until it passes
- [X] T044 [US3] Manual check of quickstart scenarios 2–5 (text, binary, large file, link/FIFO/directory) and fix
- [X] T045 [US3] Measured on Windows 11 (see research R-10: 1 MiB text shown completely in a plain TextBox, 20 000 entries expand in 1.0-1.1 s; no fallback needed); the Windows 7 SP1 measurement is left to the manual hardware checklist (T057). Original task: Measure on Windows 11 and Windows 7 SP1: a 1 MiB text file renders completely in the viewer and the 20 000-entry directory expands in under 2 s (SC-002, quickstart scenario 8); if either fails apply the planned fallback from research R-10 (`RichTextBox` as plain text; filling the tree in batches of 2 000 nodes from the message loop) in `FilesTab.cs`, and record the result in `research.md` R-10

**Checkpoint**: the right pane works for every kind of entry.

---

## Phase 6: User Story 4 - Browse a physical medium (Priority: P2)

**Goal**: choose **Device** as the source and browse and extract exactly as with an image, read-only.

**Independent Test**: a file-backed fake device with the `browse-names` content gives an identical
tree and zero write calls; removing it mid-session reports the cause.

- [X] T046 [US4] Edit `src/HddBackup.Core/Browsing/BrowseSource.cs`: `static BrowseSource OpenDevice(IDeviceAccess access, DeviceInfo device)` using `access.OpenForRead(device)` and the same partition/state/file-system logic as `OpenImage` (shared private path); a device that is gone or unreadable raises `ImagingException` (`DeviceRemoved` / `ReadError`) with the offset; never calls `OpenExclusiveForWrite`; `Ext4FileSystemView` reads surface device errors with their offset (FR-022)
- [X] T047 [P] [US4] Create `tests/HddBackup.Core.Tests/Browsing/DeviceBrowseTests.cs` with `FakeDeviceAccess` (extend `tests/HddBackup.Core.Tests/Fakes/FakeDeviceAccess.cs` only if `OpenForRead` for a file-backed image is not yet supported): tree and counts equal those of the image source; `WriteCalls == 0` through `ReadOnlyBlockDevice`; `OpenExclusiveForWrite`/lock/eject never called; the device made to fail after N reads → `ImagingException` `DeviceRemoved` from `GetChildren` and `ReadRange`, and extraction in progress fails with that code and the offset; extraction to a destination on the browsed disk refused (`DestinationIsSource`, covered with T030's setup)
- [X] T048 [US4] Add the string `Files_SourceDevice` to all six `Strings*.resx`
- [X] T049 [US4] Edit `src/HddBackup.App/Forms/FilesTab.cs`: add the **Image file** / **Device** radio pair and the device drop-down (same list and refresh as the Back up tab: reuse its enumerator and the `DeviceChangeWatcher` hookup in `src/HddBackup.App/Platform/DeviceChangeWatcher.cs`), switching sources closes the open file system, a removed device clears the tree and shows `Files_ReadError` with the cause, no write confirmation anywhere; extraction passes `BrowseDevice` in the request
- [X] T050 [US4] Run T047 and fix T046 until it passes (done: 6 tests). The manual check of quickstart scenarios 16 and 17 on a real card could not be done here (no removable media at hand): it stays in the manual hardware checklist (T057)

**Checkpoint**: images and devices browse identically.

---

## Phase 7: User Story 5 - Damaged or not cleanly unmounted file systems (Priority: P3)

**Goal**: such file systems open with the right warning; unreadable entries are marked and do not
stop browsing or extraction.

**Independent Test**: open `dirty-journal`, `orphans`, `not-clean` and `damaged-dirent` (feature 006
fixtures); check state text, browsability, per-entry marking, extraction of readable parts, image
hash unchanged.

- [X] T051 [US5] Edit `src/HddBackup.Core/Browsing/Ext4FileSystemView.cs` and `BrowseSource.cs`: mark entries whose inode flags the reader cannot serve (`Ext4Inode.FlagInlineData`, encrypted content, `FlagEaInode` content) with `NotViewable` and the reason instead of failing; make a partition whose root directory cannot be read or whose superblock fails basic checks `IsBrowsable = false` with `Unsupported` "damaged file system"; set `Counts.Approximate` for `Damaged` states; never replay the journal or write (assert in code review: no `OverlayBlockDevice`, `Jbd2Journal` or `OrphanList` use in `Browsing/`)
- [X] T052 [P] [US5] Create `tests/HddBackup.Core.Tests/Browsing/DamagedFileSystemTests.cs`: for `dirty-journal`, `orphans`, `not-clean` → state `NeedsRepair`, tree browsable, contents are the on-disk state (a file changed only in the journal shows its old content — proves no replay), image SHA-256 unchanged; `damaged-dirent` → root browsable, the damaged directory entry marked `Unreadable` with a reason, siblings readable, extraction of the parent lists the failed item and extracts the rest (SC-008); synthetic superblock with errors recorded → `Damaged` and approximate counts; a non-ext4 partition and an ext4 whose root inode is cleared → not browsable with a reason and no tree
- [X] T053 [US5] Run T052 and fix T051 until it passes; manual check of quickstart scenarios 13 and 14

**Checkpoint**: all five stories work.

---

## Phase 8: Polish & cross-cutting

- [X] T054 [P] Done: parity test passes; the Files tab was captured in all six languages through the real MainForm and checked for clipping (de, fr, ja viewed; no clipping; this also caught a missing `Files_SourceDevice` import); `docs/screenshots/files.png` (English) added. Task text: Run the resource-parity test and a manual language sweep (quickstart scenario 18): every `Files_*`, `Extract_*`, `Phase_*`, `Result_*` and `Tooltip_Files_*` key exists in all six files; take layout screenshots of the Files tab in en, de, fr, ja at 100% into `docs/screenshots/` and check for clipped text (SC-007); fix label widths or move text into tooltips
- [X] T055 [P] Update `README.md` (English): a **Files** tab section (browse, view, extract, what is not supported: `.img.gz` must be converted first, symlinks and permissions are not extracted, journal not replayed) with the new screenshot
- [X] T056 [P] Add an `## Unreleased` section to `docs/release-notes.md` for the Files tab and update `tools/tests/Get-ReleaseNotes.Tests.ps1` only if the heading rule requires it (the `Unreleased` section must not break `Get-ReleaseNotes.ps1`; check the script first)
- [X] T057 [P] Add scenarios 16, 17 and 19 of `quickstart.md` as new rows to `docs/manual-hardware-checklist.md` (real card browse read-only with before/after image hash, removal during browse, Windows 7 SP1 and Windows 11 runs of the main scenarios)
- [X] T058 Release build: 0 warnings, 0 errors; tests: 483 passed, 2 skipped (the two opt-in tests); the Docker independent check of the extracted trees passed locally (1560 and 1564 files identical to `debugfs rdump`). The `ext4-check` CI job itself has not run yet: nothing was pushed. Task text: Run `dotnet build HddBackup.sln -c Release` (0 warnings) and `dotnet test tests/HddBackup.Core.Tests -c Release`; run the Docker independent check (`quickstart.md`) locally and confirm the `ext4-check` CI job passes on the branch
- [X] T059 Measured on the 128 MiB `browse-large` fixture and on Windows 11 (research R-10): root in a few reads and well under a second (test `OpeningTheRoot_ReadsOnlyTheSuperblockAndGroupDescriptors`), 20 000-entry directory in 1.0-1.1 s. A 16-32 GiB real image over USB 2.0 was not available: it stays in the manual checklist (steps 32 and 42). Task text: Performance check against SC-001 and SC-002 on `browse-large` and on a 16–32 GiB real image over USB 2.0 (root within 5 s, 10 000-entry directory and file content within 2 s); record results in `research.md`
- [X] T060 Principle I review checklist for the change (`docs/safety-review.md` format): no write path to media in `Browsing/`, destination guards covered by tests T030 and T047, partial files cannot look complete; record it in the PR description
- [X] T061 Update `specs/008-ext4-browser/checklists/requirements.md` notes (clarifications and constitution amendment resolved) and set the spec **Status** to `Implemented` when everything above is done

---

## Dependencies & Execution Order

- **Gates**: T001 (layout approval) before every UI task; T002 (constitution 1.3.0) before any
  implementation task (it is a governance gate, not a code dependency).
- **Phase 1** (T001–T005) → **Phase 2** (T006–T010) → user stories. T003–T004 (fixtures) block all
  tests; T005 only blocks the CI comparison.
- **US1** (T011–T020) needs Phase 2. It is the MVP and a prerequisite of every other story because
  they extend `BrowseSource`, `Ext4FileSystemView` and `FilesTab`.
- **US2** (T021–T038) needs US1's view; `WindowsNames` (T021) and its tests are independent of it.
  T026–T028 (error code, request, engine) precede T036–T037 (UI).
- **US3** (T039–T045) needs US1 only; independent of US2 except that both edit `FilesTab.cs`
  (do them one after the other).
- **US4** (T046–T050) needs US1 (and US2 for the extraction guard test).
- **US5** (T051–T053) needs US1 and the 006 fixtures; the extraction part needs US2.
- **Polish** (T054–T061) after the stories it documents.

### Parallel opportunities

- Phase 2: T006, T007, T008, T009, T010 touch different files.
- US1: T013 and T014 together after T011–T012; T016 and T017 with the tests.
- US2: T021/T022 in parallel with T023–T025 work on other files; T029 and T030 together; T034 and
  T035 together.
- US3: T040 with T041.
- US4: T047 with T048.
- Polish: T054–T057 are independent documents/checks.

```text
Example, US2 in parallel:  T021 + T022 (names)   |  T034 (conflict dialog)  |  T035 (summary dialog)
then:                      T023 → T024, T025 → T026 → T027 → T028 → T029 + T030 → T031 → T032
then:                      T033 → T036 → T037 → T038
```

## Implementation Strategy

- **MVP = US1 + US2**: browse the tree and extract. Ship nothing before the constitution amendment
  (T002) and the layout approval (T001).
- Then US3 (viewer), US4 (device), US5 (damaged) in that order; each is independently testable and
  can merge as its own PR on branch `008-ext4-browser` or follow-up branches.
- Keep the extraction engine operation (T027–T028) behind the same engine and panel as the other
  operations so no UI-thread work happens during copying (Principle VI).
- Re-run the shrink and repair test suites after T006: the shared probe must change no behavior.
