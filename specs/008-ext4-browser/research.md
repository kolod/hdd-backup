# Research: Browse and Extract Files from an ext4 Partition

All unknowns of the Technical Context are resolved below. Code references are to the repository as
of 2026-11-05.

## R-1: Where the browsing logic lives, and what the UI sees

**Decision**: a new `HddBackup.Core.Browsing` namespace. The UI works with public DTOs (`FsEntry`,
`BrowsePartition`, `FilePreview`) and one facade, `Ext4FileSystemView`; no `Ext4Inode`,
`Ext4Reader` or other internal ext4 type leaves Core.

**Rationale**: the App project sees only public Core types (`InternalsVisibleTo` is for the test
project), and the ext4 types are internal on purpose. A facade keeps the WinForms code free of
format details and lets all logic be tested on fake devices (Principle VI).

**Alternatives**: making the ext4 types public — widens the surface for no gain; putting the code
in the App — untestable without UI.

## R-2: Opening sources read-only

**Decision**:

- **Image**: `FileBlockDevice.OpenRead(path)` (existing, read-only), then `PartitionTableReader.Read`
  for a whole-device image. A single-partition image (`ImageDescriptor.Kind == Partition`) is
  probed at offset 0 with `FileSystemProbe`.
- **Device**: `IDeviceAccess.OpenForRead(DeviceInfo)` (existing, used by backup) from the same
  `IDeviceEnumerator` list as the Back up tab.
- **Partitions**: every `PartitionInfo` whose `FileSystem == "ext4"` (existing probe) plus a
  superblock sanity check; other partitions are listed as "not browsable".
- **`.img.gz`**: not opened (Q2); the tab shows the existing conversion hint.

**Rationale**: all pieces exist and are exercised by backup, shrink and repair. No lock or
dismount is needed because nothing is written.

**Alternatives**: opening `.img.gz` through a streaming index — rejected by the user (Q2), and gzip
has no random access without temporary data.

## R-3: File system state without a full check

**Decision**: extract the superblock-based classification now private to `ShrinkPlanner`
(`RepairReasonsOf`, `AddFileSystemStates`) into an internal `Ext4StateProbe` used by both the
planner and the browser: `Clean`, `NeedsRepair` (journal needs recovery, orphans, not clean) or
`Damaged` (recorded errors). No journal replay, no consistency check on open.

**Rationale**: opening must be instant (SC-001); the full checker walks the whole file system.
The same states and wording as the Image tab (feature 006) keep the UI consistent, and the warning
points to the repair option there. Structural damage found later is reported per entry (R-7).

**Alternatives**: replaying the journal in memory (`OverlayBlockDevice`) to show the recovered
state — better content for power-loss cards but reads the journal (32–128 MiB) on every open and
needs the same structural check as repair; rejected for the first version (spec FR-020 says as it
is on disk) and left as a possible follow-up.

## R-4: Tree access, ordering and counts

**Decision**: `Ext4FileSystemView` reads a directory on demand with `Ext4Reader.ReadDirectory`
(which already walks plain and hashed directory blocks, as used by the shrinker). The entry type
comes from the directory entry's file-type byte, or from the inode when the `filetype` feature is
absent; symbolic-link targets are read from the inode (fast symlink) or its blocks. Entries are
sorted directories first, then by name compared ordinally on the decoded text. Names are decoded
as UTF-8; invalid bytes show as U+FFFD. All reader access is serialized with one lock because the
reader and its device are not thread-safe, and UI calls run on a worker thread with cancellation
when the selection changes.

File and directory counts (FR-008) come from the group descriptors in constant time: directories =
sum of `UsedDirs`; files = used inodes (`InodesCount − FreeInodesCount`) − directories − reserved
inodes below `FirstInode`. Exact for a consistent file system; for a damaged one the figure is
labeled approximate.

**Rationale**: a recursive count of a Raspberry Pi root file system (hundreds of thousands of
inodes) would take minutes over USB; the descriptors answer instantly.

**Alternatives**: counting while the tree is expanded — misleading; a background full walk —
needless I/O on a slow medium.

## R-5: Extraction as an engine operation

**Decision**: `OperationKind.ExtractFiles` in `ImagingEngine`. `OperationRequest` gets the source
description (image path or `DeviceInfo`), the partition, the entry path, the destination folder
and a conflict policy (`Ask`, `Replace`, `Skip`). `Validate` throws `ImagingException` for the
guards; `RunExtract` opens its own read-only source (the tab's view stays untouched), runs
`ExtractionPlanner` (preparation phase: walk, total bytes, conflicts, per-item problems), and then
`Ext4Extractor` (transfer phase). The UI asks the conflict question between `Validate`/planning and
the start of writing: the tab runs the planner synchronously-on-a-worker first (cheap: directories
only), shows `ExtractConflictDialog` if there are conflicts, then starts the engine with the chosen
policy. The result carries an `ExtractionSummary` (counts, bytes, list of issues) shown in the
panel and in `ExtractSummaryDialog`.

Files are written to `.hddbackup-part-<12 hex>` in their destination directory (a fixed-length name: a suffix on the real name would push 255-character names over the Windows limit) and renamed when complete;
on cancel or error the part file is deleted. Directory modification times are set after their
contents (post-order); file times after the rename. The progress model is the existing one:
Preparing → Transferring (bytes of all selected files) with speed, elapsed and remaining.

**Rationale**: reuses cancellation, background execution, progress, taskbar progress and result
display; keeps UI code thin; "no partly written file that looks complete" (FR-014) without a
second copy of the data. One question before writing satisfies FR-015 without blocking a
background thread on a dialog.

**Alternatives**: a separate extractor with its own progress UI — duplicates the panel; per-file
prompts during the run — blocks the worker and is tedious for directories (spec was refined to one
choice for all conflicts).

## R-6: Windows names, long paths and destinations

**Decision** (`WindowsNames`, `ExtractionPlanner`):

- Decode names as UTF-8. Replace characters invalid on Windows (`< > : " / \ | ? *`, control
  characters, U+FFFD) with `_`; a trailing dot or space becomes `_`; reserved device names
  (`CON`, `PRN`, `AUX`, `NUL`, `COM1–9`, `LPT1–9`, with or without extension) get a leading `_`.
- Within one directory, process entries in sorted order and, when a mapped name equals an earlier
  one ignoring case, append ` (2)`, ` (3)` … before the extension. Every changed name is recorded
  as a "renamed" issue with its original name.
- Create every path with the extended-length prefix (`\\?\` and `\\?\UNC\`), which works on Windows
  7 with .NET Framework 4.8; the Win10-only long-path opt-in is not used (Principle IV). Paths
  beyond the 32 767-character limit are reported as failed items.
- Destination guards: refuse when `FileIdentity.IsSameFile(destinationFile, imagePath)` can hold
  for any target file, or when `IDeviceAccess.GetPathInfo(destination).DiskNumbers` contains the
  browsed device's disk number. Free space: required bytes against `PathInfo.FreeBytes` (when
  unknown, checked on write failure). On FAT/FAT32 destinations, files of 4 GiB or more are
  reported as failed with the reason before writing.
- Symbolic links and special files are not created (listed as skipped); hard-linked inodes are
  copied once per name.

**Rationale**: collision-free by construction, deterministic, and every deviation is visible to
the user.

**Alternatives**: percent-escaping every unsafe character (injective but unreadable); creating
symbolic links (Windows privilege rules, and Linux targets are meaningless on Windows).

## R-7: Damaged entries

**Decision**: every per-entry read is wrapped; an `Ext4FormatException` or `ImagingException`
read error marks the `FsEntry` as unreadable with the message and the tree continues. Device
errors (removal, I/O) are `ImagingException` with `DeviceRemoved`/`ReadError`; they abort the
current action and show the cause with the offset (FR-022). A partition is browsable only when
`Ext4Features.Unsupported(sb)` (existing; it already names `meta_bg`, `inline_data`, `encrypt`,
`casefold`, `large_dir` and unknown flags) is empty and the block size is supported by `Ext4Reader`;
otherwise `BrowsePartition.Unsupported` carries the feature name. Per-inode flags that the reader
cannot serve (inline data, encrypted content) mark that single file as not viewable.

**Rationale**: matches FR-021/FR-022 and the existing error vocabulary.

## R-8: Constitution amendment 1.3.0 (draft)

**Decision**: amend Principle VII (MINOR, 1.2.0 → 1.3.0):

> Scope is limited to backing up and restoring USB/SD media as raw or gzip images, to shrinking
> the last ext4 partition of an image so it fits a smaller medium, to repairing that ext4 file
> system after an unclean shutdown …, **and to browsing the file system of an ext4 partition of an
> image or device and extracting individual files and directories from it, read-only; the source
> is never modified and no file is ever written into an image or onto a device by this feature.**
> Features outside that scope (file-level backup **that writes into images or devices**, cloud
> upload, scheduling, other compression formats, growing or shrinking other file systems, general
> file system repair) require a constitution amendment.

Also update the Sync Impact Report, "File systems" constraint ("ext4 is read, repaired and written
by own managed code" — add "browsed"), and the Version line. Templates: none to change.

**Status**: drafted here; to be applied as task T001 after the maintainer confirms it.

## R-9: Test strategy for the safety rules

**Decision**:

- A `ReadOnlyBlockDevice` test fake wraps `MemoryBlockDevice`/file-backed devices and fails the
  test on any `Write` or `Flush`; browsing, preview and extraction run against it (SC-004).
- A hash of the image file before and after each end-to-end test proves it is unchanged.
- Destination guard tests: destination equal to the image path, destination on the browsed disk
  (via a fake `IDeviceAccess.GetPathInfo`), insufficient space, FAT32 4 GiB limit.
- Name mapping: table-driven tests including reserved names, trailing dots, case collisions,
  invalid UTF-8, deep paths.
- Extraction: byte comparison with fixture manifests (`ManifestAssert`), cancel at every phase
  leaves no `.hddbackup-part-*` and no file that looks complete, one unreadable item does not stop
  the rest.
- Fixtures: `make-browse-fixtures.sh` creates names with spaces, UTF-8, invalid UTF-8 bytes, `:` and
  reserved names, a 300-character path, symlinks (fast and long), a FIFO and a socket, a sparse
  file, a 2 GiB-hole file, a directory with 20 000 entries, hard links; the repair fixtures
  (feature 006) are reused for User Story 5.
- CI: `check-output.sh` extended to compare the tree extracted by the tests with `debugfs rdump`
  of the source image (the same Docker job as the other independent checks).

## R-10: Viewer and tree controls

**Decision**: a standard `SplitContainer` with a `TreeView` (lazy children: a placeholder child per
directory, filled in `BeforeExpand`; `BeginUpdate`/`EndUpdate` while filling) and a read-only,
multiline monospaced `TextBox` for the viewer with `MaxLength` raised so the text fits. Auto mode
decides from the first 8 KiB: no NUL bytes and valid UTF-8 (a truncated final sequence is allowed)
→ text, else hex. Limits: text 1 MiB, hex 256 KiB (16 384 lines of 16 bytes); when the file is
larger the header states the amount shown and the full size. Hex lines: offset, 16 bytes, printable
characters.

**Risk**: a plain `TextBox` may truncate very long text on some Windows versions; the plan's
fallback is a `RichTextBox` configured as plain text (BCL, no dependency). A 20 000-entry directory
in a `TreeView` fills in about a second; if measurement on Windows 7 disagrees (quickstart scenario
8), fill in batches of 2 000 nodes from the UI message loop.

**Measured (2026-11-05, Windows 11, image on a local SSD, scripted harness)**: the plain `TextBox`
shows a 1 MiB text file completely (1 090 519 characters, 41 944 lines, including the CRLF
conversion), so the `RichTextBox` fallback is not needed here. The 20 000-entry directory expands in
1.0–1.1 s (three runs, from the click to the last node), under the 2 s of SC-002; the UI thread is
busy for about that second while the nodes are added. Windows 7 SP1 and a USB 2.0 reader are still to
be measured by hand (manual hardware checklist).

**Alternatives**: `ListView` virtual mode for the tree — no hierarchy; a third-party hex control —
violates Principle III.

## R-11: Time and metadata

**Decision**: modification time is `i_mtime` (32-bit seconds, UTC; `Ext4Inode.Mtime`) converted to
file times; the extra field (nanoseconds, epoch bits) is not read by the existing code and is not
preserved. The viewer header shows
type, size, mode as `-rw-r--r--`, numeric uid/gid and local time. Ownership, permissions and
extended attributes are not recreated on Windows (spec assumption).

## R-12: Shared image and tab integration

**Decision**: `FilesTab` takes the existing `SelectedImage` (one shared selection). Choosing a
`.img` there sets `SelectedImage.Path`, and the Restore and Image tabs follow, as today. The
device source is local to the Files tab. The tab is disabled while an extraction runs and is
rebuilt on `SelectedImage.Changed`. The `OperationPanel` instance in `MainForm` serves the new
operation; `ShowResult` receives an extra line with the summary counts and the "details" action
opens `ExtractSummaryDialog`.

**Rationale**: matches feature 004's decision and needs no new shared state.
