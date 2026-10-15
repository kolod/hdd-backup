# Research: USB/SD Card Imaging Utility

All Technical Context unknowns are resolved below. Each item: Decision / Rationale /
Alternatives considered.

## R-1. Runtime and UI framework

- **Decision**: .NET Framework 4.8, Windows Forms, C# (LangVersion default 7.3, may be raised
  for syntax-only features).
- **Rationale**: 4.8 is the newest framework supported on Windows 7 SP1 and is preinstalled on
  Windows 10 1903+ / 11, so the EXE stays tiny (< 5 MB). WinForms is built in, light, and
  supports Win7 visual styles. Targeting pack v4.8 and .NET SDK 10 are present on the dev box,
  so an SDK-style `net48` project builds with `dotnet build`.
- **Alternatives**: .NET 8/9 (no Win7 support); .NET 6 self-contained (EOL, ~60+ MB, violates
  Principle III); .NET Framework 4.0 (no zlib-backed `GZipStream`, no `CompressionLevel`, worse
  APIs — rejected unless Win7 machines without 4.8 become a hard requirement); WPF (heavier,
  weaker on Win7 low-end hardware).

## R-2. Single-executable packaging

- **Decision**: `HddBackup.App` compiles the `HddBackup.Core` sources via linked
  `<Compile Include="..\HddBackup.Core\**\*.cs" />`; no project reference, no DLLs in output.
- **Rationale**: zero tooling, zero runtime assembly-resolution tricks; Core stays a separate
  project for tests and for enforcing "no WinForms in core".
- **Alternatives**: ILMerge / Costura.Fody (extra build dependency, AV false positives);
  embedding DLL + `AppDomain.AssemblyResolve` (fragile); one project with namespaces only (no
  compile-time enforcement of UI independence).

## R-3. Embedded localization (no satellite assemblies)

- **Decision**: six `.resx` files (`Strings.resx`, `Strings.uk/de/fr/es/ja.resx`) declared as
  `EmbeddedResource` with `WithCulture="false"` and explicit `LogicalName`
  (`HddBackup.Strings.<culture>.resources`), loaded by a small `EmbeddedResourceManager`
  (subclass of `ResourceManager` overriding `InternalGetResourceSet`) with fallback to English.
  Forms do not use designer localization; each form implements `ILocalizable.ApplyStrings()`,
  called at start and on language change (runtime switch, FR-026).
- **Rationale**: keeps everything in the EXE; runtime switch without restart is trivial.
- **Alternatives**: standard satellite assemblies (create `uk\`, `de\`… folders — violates
  single-EXE); JSON string tables (loses resx tooling/translator workflow).
- **Fonts**: default `Segoe UI 9pt`; for Japanese use `Meiryo UI` → fallback `MS UI Gothic`
  (both ship with all Win7+ editions). Layouts use `TableLayoutPanel`/`AutoSize` to avoid clipping.

## R-4. Win32 APIs (all available on Windows 7 SP1)

| Need | API |
|------|-----|
| Enumerate disks | `SetupDiGetClassDevs(GUID_DEVINTERFACE_DISK)` + `SetupDiEnumDeviceInterfaces` |
| Disk number | `IOCTL_STORAGE_GET_DEVICE_NUMBER` |
| Bus type, removable, vendor/product | `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceProperty`) |
| Size, sector size | `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`; `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageAccessAlignmentProperty`, Win7+; fallback to geometry) |
| Partition layout | `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` (cross-checked by own MBR/GPT parser on sector data) |
| Write protection | `IOCTL_DISK_IS_WRITABLE` |
| Volumes on a disk | `FindFirstVolumeW`/`FindNextVolumeW` + `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` |
| System/boot/pagefile disk | `GetSystemWindowsDirectory` + `GetVolumePathName` → extents; pagefile list from `HKLM\...\Memory Management\ExistingPageFiles` (read-only) |
| Exclusive access | open volume, `FSCTL_LOCK_VOLUME` + `FSCTL_DISMOUNT_VOLUME`, keep handles until done |
| Raw I/O | `CreateFile("\\.\PhysicalDriveN")`, `FILE_FLAG_NO_BUFFERING` (+ `FILE_FLAG_WRITE_THROUGH` for writes), `ReadFile`/`WriteFile`, `SetFilePointerEx` |
| Flush | `FlushFileBuffers` on the disk handle (issues SYNCHRONIZE CACHE to the device) |
| Re-read layout after restore | `IOCTL_DISK_UPDATE_PROPERTIES` |
| Safe removal | `CM_Locate_DevNode` / `CM_Get_Parent` → `CM_Request_Device_EjectW` (veto reported); fallback `IOCTL_STORAGE_EJECT_MEDIA` for built-in card readers |
| Device change | `WM_DEVICECHANGE` (`DBT_DEVICEARRIVAL`, `DBT_DEVICEREMOVECOMPLETE`), 500 ms debounce |
| Taskbar progress | `ITaskbarList3` (COM, Win7+) |
| Destination checks | `GetDiskFreeSpaceEx`, `GetVolumeInformation` (FAT32 4 GB limit) |

- **Device filter**: list a disk iff `BusType ∈ {Usb (7), Sd (0x0C), Mmc (0x0D)}` AND it hosts
  no system, boot, or pagefile volume AND its number differs from the disk of the EXE's own
  location *for restore targets* (prevents overwriting the stick the tool runs from). USB
  external HDDs (USB bus, not removable media) are listed but tagged "external disk" and need
  an extra confirmation checkbox.
- **Rationale**: bus-type filtering is the documented way tools such as Rufus distinguish
  removable media; extents-based system-disk detection catches the boot disk in all
  configurations, including Windows-To-Go.

## R-5. I/O pipeline and throughput

- **Decision**: two-stage producer/consumer with 4 × 4 MiB page-aligned buffers
  (`VirtualAlloc`) — reader thread and writer thread connected by bounded queues. Device I/O
  is unbuffered; writes to devices use write-through. Gzip compression/decompression runs in
  the stage that touches the file. The last partial block is handled by sector-size-aligned
  transfer (device sizes are always sector multiples).
- **Rationale**: overlaps read and write, saturates USB 3 media; write-through makes transfer
  progress reflect data actually on the medium, so the final flush is short and 100% is honest
  (FR-017).
- **Alternatives**: single-threaded loop (≈ halves throughput); `FileStream` on device paths
  (works but no alignment control, buffering hides real progress); overlapped async I/O (more
  complex for the same gain).
- **Compression**: `GZipStream` (zlib-backed since 4.5), default `CompressionLevel.Optimal`,
  "Fast compression" option → `Fastest`. Uncompressed SHA-256 computed in the pipeline.
- **Multi-member gzip**: .NET Framework 4.8 `GZipStream` stops after the first gzip member, so
  images produced by `pigz`/concatenation would be truncated. Decision: the reader detects a
  following member (magic `1F 8B` after member end) and continues with a new `GZipStream`;
  covered by a unit test.
- **Large gz**: ISIZE in the gzip trailer is size mod 2³², so it is not used for totals;
  progress for gz restore uses compressed-bytes-consumed via `CountingStream` (spec assumption).

## R-6. Manifest, elevation, DPI

- **Decision**: `app.manifest` with `requestedExecutionLevel level="requireAdministrator"`,
  `dpiAware=true` (system DPI), `supportedOS` GUIDs for Win7, 8, 8.1, 10/11, and Common
  Controls v6. `AutoScaleMode.Dpi` on forms.
- **Rationale**: raw disk access requires elevation (Principle III); system DPI awareness is
  supported identically on Win7–11. Per-monitor V2 is skipped (simplicity; Win10+ only).

## R-7. Progress, speed, time estimation

- **Decision**: `PhaseModel` with weighted phases: Prepare (fixed 0.5%), Transfer (bytes),
  Verify (bytes × read/write cost ratio, initial 0.5 updated from measured speeds), Flush &
  Release & Eject (fixed reserve 1%). Displayed percent is clamped monotonic and capped at
  99.9% until the operation state is `Completed`, which alone renders 100% + "safe to remove".
  Rate: exponentially weighted moving average over ~3 s for current speed; average =
  bytes/elapsed; ETA = remaining weighted work / smoothed rate, displayed as "calculating…"
  for the first 3 s or while rate is 0. UI polls a lock-free snapshot every 250 ms (timer),
  so the I/O threads never block on the UI.
- **Rationale**: satisfies FR-015–019 and SC-004 (±15% after 10 s) with simple math.

## R-8. Sidecar formats

- **Decision** (clarified 2026-10-15): one-entry `sha256sum`-format sidecars per hashed object:
  `disk.img.sha256` for raw images; for gzip images two files — `disk.img.gz.sha256` (compressed
  file) and `disk.img.sha256` (uncompressed content = device/partition). Partition metadata in
  `<image>.partition.json` via `DataContractJsonSerializer` (includes content hash). Image and
  sidecars are written as `.partial` and renamed only on success.
- **Hashing in the same pass**: two `IncrementalHash`-style SHA-256 instances
  (`SHA256.Create()` + `TransformBlock`) — content hash fed from the read stage, file hash fed
  from a hashing wrapper stream under the file writer (bytes after gzip). Hashing runs on a
  third pipeline stage consuming the same buffers, so it overlaps I/O; SHA-256 at ~300+ MB/s per
  core is well above USB 3 media speed (SC-009 ≤ 5% overhead).
- **Restore**: the same wrappers compute file hash (bytes read from the image file) and content
  hash (decompressed bytes) while writing; compared at the end of transfer, before 100%.
- **Rationale**: interoperable, `sha256sum -c` works for the actual file in the folder; content
  hash matches the medium; JSON readable by humans.
- **Alternatives**: single sidecar with two lines (rejected by user in clarification); content
  hash only (breaks `sha256sum -c` for `.gz`); hashing in a second pass (doubles time).
- **Alternatives**: embedding metadata into a custom container (breaks interoperability with
  `dd`/Etcher, violates Principle II).

## R-9. Settings

- **Decision**: `HddBackup.ini` next to the EXE (language, auto-eject, verify-after-restore,
  verify-after-backup, fast compression, last folders). If the folder is read-only, settings
  stay in memory for the session. No registry.

## R-10. Testing approach

- **Decision**: xUnit 2.x targeting `net48`, `dotnet test`. `MemoryBlockDevice` (configurable
  sector size 512/4096, write-protect flag), `FaultyBlockDevice` (fails at offset N, simulates
  removal), `TestImageBuilder` (produces MBR and GPT layouts with known content). Native layer
  (`PhysicalDiskDevice`, enumeration, lock, eject) is covered by the manual hardware checklist
  (`docs/manual-hardware-checklist.md`) on Win7 SP1 + Win11 with real USB stick and SD card.
- **Alternatives**: VHD-attached virtual disks for automation — rejected for v1 because their
  bus type (`FileBackedVirtual`) is intentionally filtered out by the safety rules.
