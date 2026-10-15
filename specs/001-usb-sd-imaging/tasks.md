---

description: "Task list for USB/SD Card Imaging Utility"
---

# Tasks: USB/SD Card Imaging Utility

**Input**: Design documents from `/specs/001-usb-sd-imaging/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: INCLUDED — mandated by constitution Principle VI (Test-Verified Disk I/O). Tests are
written first and must fail before the corresponding implementation.

**Organization**: Tasks are grouped by user story to enable independent implementation and
testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US6 from spec.md)
- All code, comments, and docs in English (constitution Principle V)

## Path Conventions

- Core library: `src/HddBackup.Core/` (net48, no WinForms reference)
- App (single EXE, compiles Core sources via linked `<Compile Include>`): `src/HddBackup.App/`
- Tests: `tests/HddBackup.Core.Tests/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [X] T001 Create solution `HddBackup.sln` and folder layout `src/HddBackup.Core/`, `src/HddBackup.App/`, `tests/HddBackup.Core.Tests/`, `docs/` per plan.md
- [X] T002 Create SDK-style `src/HddBackup.Core/HddBackup.Core.csproj` targeting `net48`, `LangVersion` 7.3, `Nullable` off, `TreatWarningsAsErrors` true, references `System.Runtime.Serialization` only (no WinForms)
- [X] T003 Create SDK-style `src/HddBackup.App/HddBackup.App.csproj` (`net48`, `OutputType` WinExe, `UseWindowsForms` true, `AssemblyName` HddBackup, `Prefer32Bit` false, `ApplicationManifest` app.manifest) that compiles `..\HddBackup.Core\**\*.cs` via linked `<Compile Include>` and has NO ProjectReference to Core
- [X] T004 Create `tests/HddBackup.Core.Tests/HddBackup.Core.Tests.csproj` (`net48`, xUnit 2.x, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, ProjectReference to Core) and add all three projects to `HddBackup.sln`
- [X] T005 [P] Add `src/HddBackup.App/app.manifest` with `requireAdministrator`, `dpiAware` true, `supportedOS` GUIDs for Windows 7, 8, 8.1, 10/11, and Common Controls v6 dependency (research R-6)
- [X] T006 [P] Add `.editorconfig` (C# conventions, UTF-8, LF for docs) and `.gitignore` for bin/obj/.vs at repository root
- [X] T007 [P] Add `README.md` (English) with purpose, supported OS, build/test commands from quickstart.md

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure required by every user story

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Foundational tests (write first)

- [X] T008 [P] Unit tests for `MbrParser` (empty disk, MBR with 4 primaries, extended/logical, protective MBR detection) in `tests/HddBackup.Core.Tests/Partitioning/MbrParserTests.cs`
- [X] T009 [P] Unit tests for `GptParser` (valid header + entries, CRC mismatch → Unknown, backup header location, 4096-byte sectors) in `tests/HddBackup.Core.Tests/Partitioning/GptParserTests.cs`
- [X] T010 [P] Unit tests for `PhaseModel`/`RateEstimator` (monotonic percent, cap 99.9 until Completed, 100 only at Completed, "calculating" for first 3 s, ETA within ±15% after 10 s at constant simulated rate) in `tests/HddBackup.Core.Tests/Progress/ProgressModelTests.cs`
- [X] T011 [P] Unit tests for `ChecksumFile` read/write (one-entry format `<hex> *<name>\n`, accept upper-case hex, text-mode marker, CRLF; reject malformed/64-char violations) in `tests/HddBackup.Core.Tests/Formats/ChecksumFileTests.cs`
- [X] T012 [P] Unit tests for `HashingStream`/`CountingStream` (hash equals `SHA256` over same bytes, counts read/written bytes) in `tests/HddBackup.Core.Tests/Imaging/StreamWrapperTests.cs`

### Foundational implementation

- [X] T013 [P] Create `ErrorCode` enum (all codes from data-model.md OperationResult incl. `ChecksumMismatch`) and `ImagingException` (Code, Offset, Detail) in `src/HddBackup.Core/Errors/ImagingException.cs`
- [X] T014 [P] Create `IBlockDevice` interface per contracts/core-api.md in `src/HddBackup.Core/Devices/IBlockDevice.cs`
- [X] T015 [P] Create `PartitionView` (offset/length window over an `IBlockDevice`, bounds-checked, sector-aligned) in `src/HddBackup.Core/Devices/PartitionView.cs`
- [X] T016 [P] Create test fakes `MemoryBlockDevice` (sector size 512/4096, write-protect flag, read-only mode that throws on write), `FaultyBlockDevice` (fails or "disappears" at offset N) in `tests/HddBackup.Core.Tests/Fakes/`
- [X] T017 [P] Create `TestImageBuilder` producing MBR and GPT disks with known partitions and deterministic content in `tests/HddBackup.Core.Tests/Fakes/TestImageBuilder.cs`
- [X] T018 [P] Create data types `PartitionTable`, `PartitionInfo`, `TableStyle` per data-model.md in `src/HddBackup.Core/Partitioning/PartitionTable.cs`
- [X] T019 Implement `MbrParser` in `src/HddBackup.Core/Partitioning/MbrParser.cs` (makes T008 pass)
- [X] T020 Implement `GptParser` with header/entry CRC32 validation in `src/HddBackup.Core/Partitioning/GptParser.cs` and `src/HddBackup.Core/Partitioning/Crc32.cs` (makes T009 pass)
- [X] T021 Implement `PartitionTableReader` (reads sector 0/1 from `IBlockDevice`, dispatches to MBR/GPT, returns None/Unknown on damage) in `src/HddBackup.Core/Partitioning/PartitionTableReader.cs`
- [X] T022 [P] Implement `OperationState`, `OperationPhase`, immutable `OperationProgress` snapshot per data-model.md in `src/HddBackup.Core/Progress/OperationProgress.cs`
- [X] T023 Implement `RateEstimator` (EWMA ~3 s current speed, average speed, smoothed ETA, null when unknown) in `src/HddBackup.Core/Progress/RateEstimator.cs`
- [X] T024 Implement `PhaseModel` (weighted phases Prepare 0.5%, Transfer, Verify, Finalize reserve 1%; monotonic clamp; 99.9% cap until Completed; lock-free snapshot via `Interlocked`/volatile reference) in `src/HddBackup.Core/Progress/PhaseModel.cs` (makes T010 pass)
- [X] T025 [P] Implement `ChecksumFile` (Kind File/Content, read/write one-entry sha256sum format, `.partial` write + flush) in `src/HddBackup.Core/Formats/ChecksumFile.cs` (makes T011 pass)
- [X] T026 [P] Implement `CountingStream` and `HashingStream` (wraps a stream, feeds `SHA256.TransformBlock` on read/write) in `src/HddBackup.Core/Imaging/CountingStream.cs` and `src/HddBackup.Core/Imaging/HashingStream.cs` (makes T012 pass)
- [X] T027 Implement `BufferPipeline` (bounded producer/consumer over 4 × 4 MiB buffers, reader stage → hash stage → writer stage, cancellation + first-error propagation with offset) in `src/HddBackup.Core/Imaging/BufferPipeline.cs`
- [X] T028 [P] Implement Win32 P/Invoke declarations (`CreateFile`, `ReadFile`, `WriteFile`, `SetFilePointerEx`, `FlushFileBuffers`, `DeviceIoControl` + IOCTL/FSCTL constants and structs from research R-4, `VirtualAlloc`/`VirtualFree`, SetupDi*, CM_*) in `src/HddBackup.Core/Native/NativeMethods.cs` and `src/HddBackup.Core/Native/NativeStructs.cs`
- [X] T029 [P] Implement `AlignedBuffer` (page-aligned `VirtualAlloc` memory, copy to/from `byte[]`, `IDisposable`) in `src/HddBackup.Core/Native/AlignedBuffer.cs`
- [X] T030 Implement `PhysicalDiskDevice : IBlockDevice` (`\\.\PhysicalDriveN`, `FILE_FLAG_NO_BUFFERING`, write-through for writable handle, read-only open mode, sector-aligned checks, `Flush` → `FlushFileBuffers`, Win32 error → `ImagingException` with offset) in `src/HddBackup.Core/Devices/PhysicalDiskDevice.cs`
- [X] T031 Create `DeviceInfo`, `VolumeInfo`, `BusType` per data-model.md in `src/HddBackup.Core/Devices/DeviceInfo.cs`
- [X] T032 Implement `SystemDiskGuard` (disk numbers hosting Windows dir, boot volume, pagefile volumes, and the running EXE's volume) in `src/HddBackup.Core/Devices/SystemDiskGuard.cs`
- [X] T033 Implement `DeviceEnumerator : IDeviceEnumerator` (SetupDi enumeration, `IOCTL_STORAGE_GET_DEVICE_NUMBER`, `StorageDeviceProperty` bus filter Usb/Sd/Mmc, geometry/alignment, `IOCTL_DISK_IS_WRITABLE`, volumes via `FindFirstVolume` + disk extents, partition table via `PartitionTableReader`, excludes `SystemDiskGuard` disks) in `src/HddBackup.Core/Devices/DeviceEnumerator.cs`
- [X] T034 [P] Implement `EmbeddedResourceManager` (loads `HddBackup.Strings.<culture>.resources` from the EXE assembly, falls back to English) and `LanguageService` (supported cultures en/uk/de/fr/es/ja, OS UI culture detection, `LanguageChanged` event) in `src/HddBackup.App/Localization/`
- [X] T035 [P] Create `ILocalizable` (`ApplyStrings()`) and `Strings` accessor class with typed keys in `src/HddBackup.App/Localization/ILocalizable.cs` and `src/HddBackup.App/Localization/Strings.cs`
- [X] T036 Create English `src/HddBackup.App/Resources/Strings.resx` with all keys known so far and configure the App csproj to embed every `Strings*.resx` with `WithCulture="false"` and `LogicalName="HddBackup.Strings.<culture>.resources"` (research R-3)
- [X] T037 [P] Implement `PortableSettings` (read/write `HddBackup.ini` next to EXE, keys from contracts/file-formats.md, in-memory fallback if folder read-only) in `src/HddBackup.App/Settings/PortableSettings.cs`
- [X] T038 [P] Implement `ByteSizeFormatter` and `DurationFormatter` (binary units, culture-aware numbers, localized suffixes via `Strings`) in `src/HddBackup.App/Localization/Formatters.cs`
- [X] T039 [P] Implement `DeviceChangeWatcher` (`WM_DEVICECHANGE` via `NativeWindow`, 500 ms debounce, `DevicesChanged` event) in `src/HddBackup.App/Platform/DeviceChangeWatcher.cs`
- [X] T040 Implement `Program.cs` (STA, visual styles, global exception handler showing localized error, language init from settings/OS) and `MainForm` shell with Backup/Restore tabs, device list bound to `DeviceEnumerator`, auto-refresh via `DeviceChangeWatcher`, Language menu placeholder, `ILocalizable` in `src/HddBackup.App/Program.cs` and `src/HddBackup.App/Forms/MainForm.cs`
- [X] T041 Implement reusable `OperationPanel` user control (phase name, progress bar, bytes x of y, current/average speed, elapsed/remaining, Cancel button, result banner area; polls `IImagingEngine.CurrentProgress` every 250 ms) in `src/HddBackup.App/Forms/OperationPanel.cs`
- [X] T042 [P] Implement `TaskbarProgress` (`ITaskbarList3` COM interop: normal/error/paused/none states) in `src/HddBackup.App/Platform/TaskbarProgress.cs`

**Checkpoint**: Solution builds into a single `HddBackup.exe`; device list shows USB/SD media only; all foundational tests pass.

---

## Phase 3: User Story 1 - Back up an entire USB/SD medium to an image (Priority: P1) 🎯 MVP

**Goal**: Full-medium backup to `.img` / `.img.gz` with same-pass SHA-256 sidecars, live progress/speed/time, and 100% only when safe to remove.

**Independent Test**: Back up a known MBR and GPT medium to both formats; decompressed image is byte-identical to the medium; `disk.img.sha256` (and for gz, `disk.img.gz.sha256`) verify with a standard tool; at 100% the medium is released/ejected.

### Tests for User Story 1 ⚠️

- [X] T043 [P] [US1] Round-trip backup tests: {Raw, Gzip} × {MBR, GPT} × {512, 4096 sector} — image (decompressed) byte-identical to `MemoryBlockDevice` in `tests/HddBackup.Core.Tests/Imaging/BackupDeviceTests.cs`
- [X] T044 [P] [US1] Checksum tests: raw → only `disk.img.sha256`; gz → `disk.img.gz.sha256` (file hash) + `disk.img.sha256` (content hash); values equal independent SHA-256; content hash equals device hash in `tests/HddBackup.Core.Tests/Imaging/BackupChecksumTests.cs`
- [X] T045 [P] [US1] Safety/error tests: source opened read-only (write attempt on fake throws → test fails if called); read error at offset N → `Failed(ReadError, N)`, no final image or sidecars, `.partial` files deleted; destination full → `DestinationFull` in `tests/HddBackup.Core.Tests/Safety/BackupSafetyTests.cs`
- [X] T046 [P] [US1] Progress tests for backup: percent < 100 until image + sidecars flushed and renamed and device released; 100 at Completed in `tests/HddBackup.Core.Tests/Progress/BackupProgressTests.cs`

### Implementation for User Story 1

- [X] T047 [P] [US1] Implement `OperationRequest`, `OperationResult`, `OperationKind`, `CompressionChoice` per data-model.md in `src/HddBackup.Core/Imaging/OperationRequest.cs` and `src/HddBackup.Core/Imaging/OperationResult.cs`
- [X] T048 [P] [US1] Implement `ImageWriter` (opens `<image>.partial`, optional `GZipStream` with `CompressionLevel` and `FNAME`, `HashingStream` below gzip for file hash, `Flush(true)` on complete, rename sidecars then image, delete partials on abort) in `src/HddBackup.Core/Imaging/ImageWriter.cs`
- [X] T049 [P] [US1] Implement `DestinationChecks` (free space via `GetDiskFreeSpaceEx`, FAT32 4 GB limit via `GetVolumeInformation`, image destination not on source device) in `src/HddBackup.Core/Imaging/DestinationChecks.cs`
- [X] T050 [US1] Implement `ImagingEngine.RunAsync` backup path for `BackupDevice` (open read-only, pipeline device→hash(content)→ImageWriter, phases Preparing/Transferring/Flushing/Releasing/Ejecting, write checksum sidecars via `ChecksumFile`, error/offset mapping) in `src/HddBackup.Core/Imaging/ImagingEngine.cs` (makes T043–T046 pass)
- [X] T051 [US1] Implement `DeviceAccess.OpenForRead` and `DeviceAccess.Eject` (`CM_Locate_DevNode` → `CM_Get_Parent` → `CM_Request_Device_EjectW`, veto name capture, fallback `IOCTL_STORAGE_EJECT_MEDIA`) in `src/HddBackup.Core/Devices/DeviceAccess.cs` and `src/HddBackup.Core/Devices/DeviceEjector.cs`
- [X] T052 [US1] Build Backup tab UI: scope (whole device), image path + Browse (`.img`/`.img.gz` filter), "Fast compression", "Eject when finished", "Verify after backup" options bound to `PortableSettings`, Start button with pre-checks from `DestinationChecks` (overwrite confirmation covers sidecars) in `src/HddBackup.App/Forms/BackupTab.cs`
- [X] T053 [US1] Wire Backup tab to `ImagingEngine` + `OperationPanel` + `TaskbarProgress`; completion banner "safe to remove", content SHA-256 with Copy button, eject-veto warning in `src/HddBackup.App/Forms/BackupTab.cs` and `src/HddBackup.App/Forms/OperationPanel.cs`
- [X] T054 [US1] Implement optional verify-after-backup (re-read produced image file and compare to computed hashes; off by default) in `src/HddBackup.Core/Imaging/ImagingEngine.cs`
- [X] T055 [US1] Add all US1 string keys (labels, phases, errors, banners) to `src/HddBackup.App/Resources/Strings.resx`

**Checkpoint**: MVP — full-medium backup works end-to-end on real hardware (quickstart manual step 3).

---

## Phase 4: User Story 2 - Restore an image to a new medium (Priority: P1)

**Goal**: Write `.img`/`.img.gz` (streamed) to a removable medium with confirmation, lock/dismount, size checks, same-pass image checksum check, optional read-back verify, and honest 100%.

**Independent Test**: Restore US1 images (both formats) to another medium; dump equals image; corrupted image → `ChecksumMismatch`; pulling the medium right at 100% loses nothing.

### Tests for User Story 2 ⚠️

- [X] T056 [P] [US2] Round-trip restore tests: {Raw, Gzip} × {MBR, GPT} × {512, 4096} onto equal and larger `MemoryBlockDevice`; gz restore creates no temp file in `tests/HddBackup.Core.Tests/Imaging/RestoreDeviceTests.cs`
- [X] T057 [P] [US2] `RestoreGuard` tests: oversize refused, write-protected refused, `HostsExecutable` refused, image stored on target refused, external disk without confirmation refused — zero bytes written in each case in `tests/HddBackup.Core.Tests/Safety/RestoreGuardTests.cs`
- [X] T058 [P] [US2] Restore checksum tests: matching sidecars → verified; single-byte corruption → `Failed(ChecksumMismatch)` with device flushed and released; no sidecars → `NotAvailable` + computed hash; only one of two gz sidecars present → that one checked in `tests/HddBackup.Core.Tests/Imaging/RestoreChecksumTests.cs`
- [X] T059 [P] [US2] Gzip reader tests: multi-member gzip restored completely; truncated/corrupt gz → `CorruptImage` with offset; logical size > 4 GB via synthetic stream in `tests/HddBackup.Core.Tests/Imaging/GzipReaderTests.cs`
- [X] T060 [P] [US2] Read-back verify tests: mismatch injected on device → `Failed(VerifyMismatch, offset)`; progress < 100 until verify + flush + release done in `tests/HddBackup.Core.Tests/Imaging/RestoreVerifyTests.cs`
- [X] T061 [P] [US2] Lock failure test: `IDeviceAccess` fake throwing `LockFailed` → no write calls on device in `tests/HddBackup.Core.Tests/Safety/LockFailureTests.cs`

### Implementation for User Story 2

- [X] T062 [P] [US2] Implement `ImageReader` (detects raw/gzip by magic, `CountingStream` for compressed progress, `HashingStream` for file hash, multi-member gzip continuation, maps decode errors to `CorruptImage` + offset) in `src/HddBackup.Core/Imaging/ImageReader.cs`
- [X] T063 [P] [US2] Implement `ImageDescriptor` loading (format, kind, sizes, `FileChecksum`/`ContentChecksum` discovery per contracts/file-formats.md table) in `src/HddBackup.Core/Imaging/ImageDescriptor.cs`
- [X] T064 [P] [US2] Implement `VerificationResult` and checksum comparison helper in `src/HddBackup.Core/Formats/VerificationResult.cs`
- [X] T065 [US2] Implement `RestoreGuard` (all validation rules from data-model.md OperationRequest) in `src/HddBackup.Core/Imaging/RestoreGuard.cs` (makes T057 pass)
- [X] T066 [US2] Implement `DeviceLock`/`IExclusiveDevice` and `DeviceAccess.OpenExclusiveForWrite` (lock + dismount every volume, keep handles, abort on failure, `IOCTL_DISK_UPDATE_PROPERTIES` on dispose) in `src/HddBackup.Core/Devices/DeviceLock.cs` and `src/HddBackup.Core/Devices/DeviceAccess.cs`
- [X] T067 [US2] Implement `ImagingEngine` restore path for `RestoreDevice` (Validate → exclusive open → pipeline ImageReader→hash(content)→device write-through → compare checksums → Flush → optional read-back verify → release → eject; size-unknown gz progress by compressed bytes) in `src/HddBackup.Core/Imaging/ImagingEngine.cs` (makes T056, T058–T061 pass)
- [X] T068 [US2] Implement `ConfirmWriteDialog` (target model/capacity/bus/letters/partitions, image name/size, larger-target warning, external-disk checkbox, Cancel default, Write enabled after 2 s) in `src/HddBackup.App/Forms/ConfirmWriteDialog.cs`
- [X] T069 [US2] Build Restore tab UI (image picker with detected format/size/checksum availability, target device list excluding `HostsExecutable`, "Verify after write" and "Eject when finished" options, Write button → `RestoreGuard` → `ConfirmWriteDialog` → engine + `OperationPanel`; result lists each checksum status) in `src/HddBackup.App/Forms/RestoreTab.cs`
- [X] T070 [US2] Add all US2 string keys to `src/HddBackup.App/Resources/Strings.resx`

**Checkpoint**: US1 + US2 = complete backup/restore product (quickstart manual step 4).

---

## Phase 5: User Story 3 - Back up and restore a single partition (Priority: P2)

**Goal**: Partition-scoped backup/restore with `.partition.json` metadata (incl. content hash) and isolation of other partitions.

**Independent Test**: Back up partition 2 of a known medium; image equals its sector range; restore into a matching partition on another medium; table and other partitions byte-identical.

### Tests for User Story 3 ⚠️

- [X] T071 [P] [US3] Partition round-trip tests {Raw, Gzip} × {MBR, GPT}: image equals sector range; `.partition.json` fields and content sha256 correct in `tests/HddBackup.Core.Tests/Imaging/PartitionBackupTests.cs`
- [X] T072 [P] [US3] Partition restore isolation tests: only target range changes; smaller target partition refused; missing `.partition.json` refused; `formatVersion` > 1 refused in `tests/HddBackup.Core.Tests/Imaging/PartitionRestoreTests.cs`

### Implementation for User Story 3

- [X] T073 [P] [US3] Implement `PartitionMetadata` + `PartitionMetadataFile` (DataContractJson, schema from contracts/file-formats.md, `.partial` + flush + rename) in `src/HddBackup.Core/Formats/PartitionMetadataFile.cs`
- [X] T074 [US3] Extend `ImagingEngine` with `BackupPartition` and `RestorePartition` using `PartitionView`, metadata sidecar, partition-level `RestoreGuard` rules in `src/HddBackup.Core/Imaging/ImagingEngine.cs` and `src/HddBackup.Core/Imaging/RestoreGuard.cs`
- [X] T075 [US3] Add scope selector (Whole device / Partition) and partition grid (No., label, FS, size, offset; explanation when table None/Unknown) to Backup and Restore tabs in `src/HddBackup.App/Forms/PartitionGrid.cs`, `src/HddBackup.App/Forms/BackupTab.cs`, `src/HddBackup.App/Forms/RestoreTab.cs`
- [X] T076 [US3] Add US3 string keys to `src/HddBackup.App/Resources/Strings.resx`

**Checkpoint**: Partition imaging works independently of full-device flows.

---

## Phase 6: User Story 4 - Use the utility in the user's language on any supported Windows (Priority: P2)

**Goal**: Six fully translated UI languages, runtime switching, no clipping; same EXE runs on Windows 7 SP1 – 11 without installation.

**Independent Test**: Run EXE from a USB stick on clean Win7 SP1 and Win11; switch through all six languages; nothing untranslated or clipped; no files outside EXE folder except chosen images.

### Tests for User Story 4 ⚠️

- [X] T077 [P] [US4] Resource parity test: all six `Strings*.resx` (read as XML from `src/HddBackup.App/Resources/`) have identical key sets, no empty values, identical `{n}` placeholder sets per key in `tests/HddBackup.Core.Tests/Localization/ResourceParityTests.cs`
- [X] T078 [P] [US4] Build artifact test script: Release output contains `HddBackup.exe` < 5 MB and no culture subfolders/DLLs in `tests/check-artifact.ps1`

### Implementation for User Story 4

- [X] T079 [P] [US4] Create `src/HddBackup.App/Resources/Strings.uk.resx` (Ukrainian) with all keys
- [X] T080 [P] [US4] Create `src/HddBackup.App/Resources/Strings.de.resx` (German) with all keys
- [X] T081 [P] [US4] Create `src/HddBackup.App/Resources/Strings.fr.resx` (French) with all keys
- [X] T082 [P] [US4] Create `src/HddBackup.App/Resources/Strings.es.resx` (Spanish) with all keys
- [X] T083 [P] [US4] Create `src/HddBackup.App/Resources/Strings.ja.resx` (Japanese) with all keys
- [X] T084 [US4] Implement Language menu (native language names), runtime switch calling `ApplyStrings()` on all open forms/controls, persistence in `PortableSettings` in `src/HddBackup.App/Forms/MainForm.cs`
- [X] T085 [US4] Implement font selection (`Segoe UI 9pt`; Japanese → `Meiryo UI` → `MS UI Gothic` fallback) and convert all forms to `TableLayoutPanel`/`AutoSize` layouts with `AutoScaleMode.Dpi` in `src/HddBackup.App/Localization/UiFont.cs` and `src/HddBackup.App/Forms/*.cs`
- [X] T086 [US4] Verify no Win8+-only APIs are called unconditionally (audit `NativeMethods.cs`; feature-detect `StorageAccessAlignmentProperty` with geometry fallback) in `src/HddBackup.Core/Native/NativeMethods.cs` and `src/HddBackup.Core/Devices/DeviceEnumerator.cs`

**Checkpoint**: All UI available in six languages; artifact is a single small EXE.

---

## Phase 7: User Story 5 - Cancel an operation safely (Priority: P3)

**Goal**: Cancel at any time; backup leaves no image/sidecars; restore flushes and releases and reports incomplete content; window close is guarded.

**Independent Test**: Cancel each operation at random points; verify state, files, and medium release.

### Tests for User Story 5 ⚠️

- [X] T087 [P] [US5] Cancellation tests at random offsets for backup (no final image, no sidecars, no `.partial` files) and restore (`Cancelled`, `SafeToRemove` true, `ContentComplete` false, flush called before release) in `tests/HddBackup.Core.Tests/Imaging/CancellationTests.cs`
- [X] T088 [P] [US5] Device-removed-mid-run tests via `FaultyBlockDevice` (`Failed(DeviceRemoved, offset)`, engine stays responsive, `SafeToRemove` reports "already removed") in `tests/HddBackup.Core.Tests/Safety/DeviceRemovedTests.cs`

### Implementation for User Story 5

- [X] T089 [US5] Harden cancellation/cleanup paths in `ImagingEngine` and `BufferPipeline` (always flush + release after any device write; delete partials; final state mapping) in `src/HddBackup.Core/Imaging/ImagingEngine.cs` and `src/HddBackup.Core/Imaging/BufferPipeline.cs`
- [X] T090 [US5] UI: Cancel confirmation for restore ("target will be left incomplete"), taskbar paused state while confirming, Cancelled/Failed banners with safe-to-remove and content-complete lines in `src/HddBackup.App/Forms/OperationPanel.cs`
- [X] T091 [US5] Guard window closing during operations (`FormClosing`: confirm → cancel → wait for engine finalize → close) in `src/HddBackup.App/Forms/MainForm.cs`
- [X] T092 [US5] Add US5 string keys to all six `src/HddBackup.App/Resources/Strings*.resx`

**Checkpoint**: Every operation can be interrupted safely.

---

## Phase 8: User Story 6 - Verify an existing image without restoring (Priority: P3)

**Goal**: "Verify image" recomputes checksums of an image file and compares with sidecars; offers to save sidecars when missing.

**Independent Test**: Verify a good image (all Match) and a 1-byte-corrupted copy (Mismatch); image without sidecars → hashes shown, Save creates correct sidecars.

### Tests for User Story 6 ⚠️

- [X] T093 [P] [US6] VerifyImage tests: Match/Mismatch/NotAvailable per sidecar; never opens a device; saved sidecars match FR-028/FR-029 layout in `tests/HddBackup.Core.Tests/Imaging/VerifyImageTests.cs`

### Implementation for User Story 6

- [X] T094 [US6] Implement `VerifyImage` operation in `ImagingEngine` (ImageReader → hash file + content, progress/speed/time, results list) and `SaveChecksumFiles` helper in `src/HddBackup.Core/Imaging/ImagingEngine.cs` and `src/HddBackup.Core/Formats/ChecksumFile.cs`
- [X] T095 [US6] Add "Verify Image" button on Restore tab (enabled with image selected, no device needed), results display, "Save checksum files" action in `src/HddBackup.App/Forms/RestoreTab.cs`
- [X] T096 [US6] Add US6 string keys to all six `src/HddBackup.App/Resources/Strings*.resx`

**Checkpoint**: All six user stories functional.

---

## Phase 9: Polish & Cross-Cutting Concerns

- [X] T097 [P] Write `docs/manual-hardware-checklist.md` from quickstart.md manual steps 1–8 (Win7 SP1 + Win11, USB stick + SD card, pull-at-100% test, throughput vs. reference tool, SC-009 checksum overhead)
- [X] T098 [P] Add `AboutDialog` (version, license, link to README) with localized strings in `src/HddBackup.App/Forms/AboutDialog.cs`
- [ ] T099 *(pending real hardware; default 4 × 4 MiB buffers in place)* Performance pass: measure throughput vs. Win32 Disk Imager and checksum overhead; tune buffer count/size in `src/HddBackup.Core/Imaging/BufferPipeline.cs` to meet SC-003 and SC-009
- [X] T100 Safety review against constitution Principle I checklist for all device write paths; record findings in `docs/safety-review.md`
- [ ] T101 *(partial: tests, artifact check and hash recorded in docs/release-notes.md; hardware checklist pending)* Run full `dotnet test` and quickstart.md validation; record SHA-256 of release `HddBackup.exe` and hardware checklist results in `docs/release-notes.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies
- **Foundational (Phase 2)**: depends on Setup — BLOCKS all user stories
- **US1 (Phase 3)**: after Foundational
- **US2 (Phase 4)**: after Foundational; reuses `OperationRequest/Result` (T047) and `DeviceAccess` (T051) from US1 — implement US1 first or pull T047/T051 forward
- **US3 (Phase 5)**: after US1 + US2 (extends both engine paths)
- **US4 (Phase 6)**: translations (T079–T083) best after string keys of US1–US3 stabilize; T077/T078/T086 can start after Foundational
- **US5 (Phase 7)**: after US1 + US2
- **US6 (Phase 8)**: after US2 (needs `ImageReader`, `ImageDescriptor`, `VerificationResult`)
- **Polish (Phase 9)**: after desired stories

### Within Each User Story

- Tests first, must fail → data types → core services → engine path → UI → strings
- `ImagingEngine.cs` is touched by T050, T054, T067, T074, T089, T094 — these are sequential (same file)

### Parallel Opportunities

- Setup: T005, T006, T007
- Foundational tests T008–T012 together; then T013–T018, T022, T025, T026, T028, T029, T034, T035, T037, T038, T039, T042 in parallel
- US1 tests T043–T046 together; T047–T049 together
- US2 tests T056–T061 together; T062–T064 together
- US4 translations T079–T083 fully parallel

---

## Parallel Example: User Story 2

```text
# Tests together:
Task: "Round-trip restore tests in tests/HddBackup.Core.Tests/Imaging/RestoreDeviceTests.cs"
Task: "RestoreGuard tests in tests/HddBackup.Core.Tests/Safety/RestoreGuardTests.cs"
Task: "Restore checksum tests in tests/HddBackup.Core.Tests/Imaging/RestoreChecksumTests.cs"
Task: "Gzip reader tests in tests/HddBackup.Core.Tests/Imaging/GzipReaderTests.cs"

# Independent core pieces together:
Task: "ImageReader in src/HddBackup.Core/Imaging/ImageReader.cs"
Task: "ImageDescriptor in src/HddBackup.Core/Imaging/ImageDescriptor.cs"
Task: "VerificationResult in src/HddBackup.Core/Formats/VerificationResult.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 Setup → Phase 2 Foundational
2. Phase 3 US1 → validate with quickstart manual step 3 on real SD card/USB stick
3. Stop and review before writing to devices (US2 is the first destructive path)

### Incremental Delivery

1. Setup + Foundational → device list visible
2. + US1 → backups (MVP)
3. + US2 → restore (complete core product)
4. + US3 → partitions; + US4 → six languages / Win7 validation
5. + US5 → hardened cancellation; + US6 → standalone image verification
6. Polish → release with hash and hardware checklist

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- Native device code (`PhysicalDiskDevice`, `DeviceEnumerator`, `DeviceLock`, `DeviceEjector`) is not unit-testable; it is covered by `docs/manual-hardware-checklist.md`
- Never test write paths against real disks other than dedicated expendable USB/SD media
- Strings added in US1–US3 are English-only until T079–T083; tracked as TODO per constitution V
