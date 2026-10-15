# Implementation Plan: USB/SD Card Imaging Utility

**Branch**: `001-usb-sd-imaging` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-usb-sd-imaging/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

A portable single-executable Windows utility that backs up removable USB/SD media (whole device
or one partition) to `.img` / `.img.gz` and restores such images (streaming directly from
`.img.gz`) to a new medium. Progress, current/average speed, elapsed and remaining time are
shown for every operation, and 100% is shown only once the medium is safe to remove (data
written through, caches flushed, verified, handles released, device ejected when enabled).

Technical approach: C# on .NET Framework 4.8 with Windows Forms. Raw device access via Win32
P/Invoke (`\\.\PhysicalDriveN`, unbuffered write-through I/O, volume lock/dismount). Imaging
logic lives in a UI-independent core behind an `IBlockDevice` abstraction so it is unit-testable
with in-memory devices. Core sources are compiled into the single executable; localized strings
are embedded resources loaded by a custom resource manager (no satellite folders).

## Technical Context

**Language/Version**: C# 7.3 (default for net48; `LangVersion` MAY be raised to 10 for
syntax-only features that need no newer runtime types)

**Primary Dependencies**: .NET Framework 4.8 BCL only — Windows Forms,
`System.IO.Compression.GZipStream`, `System.Security.Cryptography.SHA256`,
`System.Runtime.Serialization.Json`; Win32 via P/Invoke (`kernel32`, `setupapi`, `cfgmgr32`,
`shell32` `ITaskbarList3`). Test-only: xUnit 2.x, Microsoft.NET.Test.Sdk.

**Storage**: image files chosen by the user + sidecars (`.sha256`, `.partition.json`);
optional `HddBackup.ini` next to the executable.

**Testing**: xUnit on `net48` (`dotnet test`), in-memory/file-backed fake block devices;
manual hardware checklist on Windows 7 SP1 and Windows 11.

**Target Platform**: Windows 7 SP1, 8.1, 10, 11 — x86 and x64 (AnyCPU, Prefer32Bit=false).

**Project Type**: desktop-app (single portable GUI executable).

**Performance Goals**: ≥ 90% of the medium's raw sequential throughput for `.img`
(SC-003); UI progress refresh ≥ 1 Hz without stalling I/O.

**Constraints**: single EXE < 5 MB, no installer/registry, `requireAdministrator`, no temp
files during `.img.gz` restore, memory < 100 MB working set.

**Scale/Scope**: media up to 2 TB (SDXC limit), images > 4 GB, 6 UI languages, ~4 screens.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Gate | Pre-design | Post-design |
|---|-----------|------|------------|-------------|
| I | Data Safety First | Only USB/SD bus devices listed; system/boot/pagefile disks excluded; explicit confirmation; lock+dismount or abort; size checks; read-only source; errors report offset; cancel supported | ✅ planned (FR-001–006, 021–023) | ✅ `DeviceEnumerator` filter + `SystemDiskGuard`, `DeviceLock`, `RestoreGuard` in [contracts/core-api.md](contracts/core-api.md) |
| II | Faithful Imaging | Sector 0 → last sector; MBR+GPT; per-partition with metadata; `.img`/`.img.gz` RFC 1952; streaming gz restore; aligned I/O; hash + read-back verify | ✅ planned (FR-007–014) | ✅ [contracts/file-formats.md](contracts/file-formats.md), `ImagingEngine` pipeline |
| III | Portable & Compact | Single EXE, embedded languages, no registry, no 3rd-party runtime deps, < 5 MB, requireAdministrator, DPI-aware | ✅ | ✅ source-linked core, `EmbeddedResourceManager`, manifest in research R-3/R-6 |
| IV | Win7–Win11 | Only Win7-available APIs unconditionally | ✅ all APIs in research R-4 exist on Win7 SP1 | ✅ |
| V | English-first, localized UI | English code/docs; 6 languages; no hard-coded strings; runtime switch; no clipping | ✅ | ✅ resource-key parity test, `ILocalizable` forms, font fallback for Japanese |
| VI | Test-Verified Disk I/O | Core behind `IBlockDevice`; round-trip tests for img/gz × full/partition × MBR/GPT; safety rule tests; manual HW checklist | ✅ | ✅ test matrix in [quickstart.md](quickstart.md) |
| VII | Simplicity | Scope limited; no speculative abstractions | ✅ one exe project + one core library (for test isolation) + one test project | ✅ |

**Result**: PASS — no violations; Complexity Tracking not required.

## Project Structure

### Documentation (this feature)

```text
specs/001-usb-sd-imaging/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   ├── core-api.md      # UI ↔ core contract (interfaces, progress, errors)
│   ├── file-formats.md  # .img / .img.gz / .sha256 / .partition.json
│   └── ui.md            # Screens, states, localization keys contract
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
HddBackup.sln
src/
├── HddBackup.Core/                 # net48 class library, NO WinForms reference
│   ├── Devices/                    # IBlockDevice, DeviceInfo, DeviceEnumerator, SystemDiskGuard,
│   │                               # DeviceLock, DeviceEjector, PhysicalDiskDevice
│   ├── Partitioning/               # MbrParser, GptParser, PartitionTable, PartitionInfo
│   ├── Imaging/                    # ImagingEngine (backup/restore/verify), BufferPipeline,
│   │                               # ImageReader/ImageWriter (.img/.img.gz), CountingStream
│   ├── Formats/                    # ChecksumFile (.sha256), PartitionMetadataFile (.json)
│   ├── Progress/                   # OperationProgress, PhaseModel, RateEstimator
│   ├── Errors/                     # ImagingException + ErrorCode
│   └── Native/                     # P/Invoke declarations, SafeHandles, AlignedBuffer
└── HddBackup.App/                  # net48 WinExe; compiles ../HddBackup.Core/**/*.cs (linked)
    ├── Forms/                      # MainForm, ConfirmWriteDialog, OperationPanel, AboutDialog
    ├── Localization/               # EmbeddedResourceManager, LanguageService, ILocalizable
    ├── Resources/                  # Strings.resx, Strings.uk.resx, .de, .fr, .es, .ja
    ├── Platform/                   # DeviceChangeWatcher (WM_DEVICECHANGE), TaskbarProgress
    ├── Settings/                   # PortableSettings (HddBackup.ini)
    ├── app.manifest                # requireAdministrator, dpiAware, supportedOS Win7–Win10+
    └── Program.cs
tests/
└── HddBackup.Core.Tests/           # net48 xUnit; references HddBackup.Core project
    ├── Fakes/                      # MemoryBlockDevice, FaultyBlockDevice, TestImageBuilder
    ├── Partitioning/
    ├── Imaging/                    # round-trip matrix, cancellation, error offsets
    ├── Progress/
    ├── Safety/                     # one test per Principle I rule
    └── Localization/               # key parity across 6 resx files (reads App resx as files)
docs/
└── manual-hardware-checklist.md
```

**Structure Decision**: Single desktop application. `HddBackup.Core` exists as a separate
project only to (a) guarantee no UI dependency and (b) be referenced by tests; the shipped
`HddBackup.App.exe` compiles the same source files directly (linked `<Compile Include>`), so the
deliverable stays one executable with no DLLs (Principle III).

## Complexity Tracking

No constitution violations to justify.
