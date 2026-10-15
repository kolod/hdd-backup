<!--
Sync Impact Report
==================
Version change: 1.0.0 → 1.1.0 (2026-10-15)
Bump rationale: MINOR — scope of Principle VII materially expanded (feature 002).
Modified principles:
- VII. Simplicity — scope now also covers shrinking the last ext4 partition of an image
  (rebuild into a new image; the original is never modified).
Modified sections:
- Technology & Platform Constraints — ext4 handled by own managed code; developer-only Linux
  tooling (Docker) allowed for test fixtures and independent checks, never used by the product.
Templates requiring updates: none (plan Constitution Check is derived at plan time).
Deferred TODOs: none.

Previous report (1.0.0):
Version change: (template, unversioned) → 1.0.0
Bump rationale: initial ratification; all placeholders replaced with concrete principles.

Modified principles (template slot → adopted title):
- [PRINCIPLE_1_NAME] → I. Data Safety First (NON-NEGOTIABLE)
- [PRINCIPLE_2_NAME] → II. Faithful Imaging (Whole-Disk & Per-Partition)
- [PRINCIPLE_3_NAME] → III. Portable & Compact
- [PRINCIPLE_4_NAME] → IV. Windows 7 – Windows 11 Compatibility
- [PRINCIPLE_5_NAME] → V. English-First Engineering, Localized UI
Added principles:
- VI. Test-Verified Disk I/O
- VII. Simplicity

Added sections:
- Technology & Platform Constraints (was [SECTION_2_NAME])
- Development Workflow & Quality Gates (was [SECTION_3_NAME])
- Governance (filled)

Removed sections: none

Templates requiring updates:
- ✅ .specify/templates/plan-template.md — Constitution Check is derived from this file at plan
  time; no static edits required.
- ✅ .specify/templates/spec-template.md — generic; no mandatory sections added/removed.
- ✅ .specify/templates/tasks-template.md — generic; principle-driven task types (localization,
  hardware verification) are generated per feature by /speckit-tasks.
- ✅ .specify/templates/checklist-template.md — generic; no changes.
- ⚠ .specify/templates/commands/*.md — directory not present; nothing to verify.
- ⚠ README.md — not present yet; create it in English when the first feature lands.

Deferred TODOs: none.
-->

# hdd-backup Constitution

## Core Principles

### I. Data Safety First (NON-NEGOTIABLE)

The tool writes raw sectors to physical devices; a single mistake destroys user data.

- The application MUST only list removable media (USB mass storage, SD/microSD card readers)
  as write targets by default. Fixed disks, the system disk, and the disk hosting the
  Windows/boot volume MUST NEVER be offered as a write target.
- Every write (restore) operation MUST show the target device's model, size, bus type, and
  current partitions, and MUST require an explicit confirmation step before any byte is written.
- Before writing, the application MUST lock and dismount all volumes of the target device and
  MUST abort cleanly if locking fails.
- The application MUST refuse to restore an image larger than the target device and MUST warn
  when the target is larger (unallocated space remains).
- The source device MUST be opened read-only during backup; the backup path MUST never write to
  the source.
- Errors (I/O failure, device removal, out-of-space on destination) MUST stop the operation,
  report the exact offset and cause, and leave partial output clearly marked as incomplete
  (never silently delivered as a valid image).
- Cancellation MUST be supported at any time and MUST leave the system in a defined state.

**Rationale**: irreversible data loss is the only failure mode that cannot be fixed by a patch.

### II. Faithful Imaging (Whole-Disk & Per-Partition)

- **Full device backup** MUST copy the device sector-for-sector from LBA 0 to the last sector,
  including the partition table (MBR and GPT, with GPT backup header), boot sectors, gaps, and
  hidden/unknown partitions. The resulting image MUST be restorable to a bit-identical device.
- **Partition backup** MUST copy a single selected partition's sector range exactly and MUST
  record enough metadata (partition type/GUID, size, offset, source table style) to restore it.
- Supported output formats are exactly: uncompressed raw `*.img` and gzip-compressed `*.img.gz`
  (RFC 1952, readable by standard `gzip`/`7-Zip`). Images MUST be interoperable with common
  tools (e.g., `dd`, Win32 Disk Imager, Rufus, balenaEtcher).
- **Restore** MUST support writing both `*.img` and `*.img.gz` to a new device, and `*.img.gz`
  MUST be decompressed as a stream directly to the device — no temporary uncompressed file.
- All I/O MUST be sector-aligned and use the device's reported logical sector size.
- Backup and restore MUST offer integrity verification (hash computed during the operation and,
  for restore, an optional read-back compare against the source image).

**Rationale**: an image that is not exact or not restorable is worse than no backup.

### III. Portable & Compact

- The deliverable MUST be a single executable that runs without installation (copy-and-run from
  any folder or USB stick). Localized UI resources MUST be embedded in that executable.
- The application MUST NOT write to the registry, install services/drivers, or leave files
  outside its own folder and the user-chosen image paths. Optional settings MAY be stored in a
  file next to the executable.
- Third-party runtime dependencies MUST be avoided; any exception MUST be embedded, license-
  compatible, and justified in the feature plan. Executable size SHOULD stay under 5 MB.
- The executable MUST declare `requireAdministrator` in its manifest, since raw device access
  requires elevation, and MUST be DPI-aware.

**Rationale**: the tool is used in recovery situations on arbitrary machines; zero setup matters.

### IV. Windows 7 – Windows 11 Compatibility

- Every supported OS version — Windows 7 SP1, 8.1, 10, 11 (x86 and x64) — MUST be able to run
  the same executable.
- Only Win32 APIs available on Windows 7 SP1 MAY be used unconditionally (e.g., `CreateFile` on
  `\\.\PhysicalDriveN`, `DeviceIoControl` with `IOCTL_DISK_GET_DRIVE_LAYOUT_EX`,
  `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`, `FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`,
  `IOCTL_STORAGE_QUERY_PROPERTY`). Newer APIs MUST be feature-detected with a fallback.
- Any feature that cannot work on Windows 7 MUST be justified in the plan and degrade
  gracefully, never crash.

**Rationale**: the target audience includes legacy and offline machines.

### V. English-First Engineering, Localized UI

- All source code identifiers, code comments, commit messages, specifications, plans, task lists,
  README, and other documentation MUST be written in English, regardless of the language used in
  requests or discussions.
- The UI default language MUST be English. The UI MUST also be fully localized into Ukrainian,
  German, French, Spanish, and Japanese.
- No user-visible string MAY be hard-coded; all UI text, including error messages and
  confirmation dialogs, MUST come from resource tables. Adding a string requires adding it for
  all six languages (English text as fallback is permitted only as an explicitly tracked TODO).
- The UI language MUST follow the OS UI culture when supported, otherwise English, and MUST be
  switchable manually at runtime.
- Layouts MUST accommodate longer translations (German/French) and CJK fonts (Japanese) without
  clipping.

**Rationale**: one working language keeps the codebase maintainable; localization reaches users.

### VI. Test-Verified Disk I/O

- Core imaging logic (partition-table parsing, sector range calculation, gzip streaming,
  hashing, size checks) MUST live in a UI-independent layer behind an abstraction of a "block
  device" so it can be unit-tested against in-memory and file-backed fake devices.
- Round-trip tests (backup → restore → byte compare) MUST exist for both `*.img` and
  `*.img.gz`, for full-device and per-partition modes, and for MBR and GPT layouts.
- Every safety rule in Principle I MUST have at least one automated test.
- Before a release, a manual hardware checklist MUST be executed on real USB and SD media on
  at least Windows 7 SP1 and Windows 11.

**Rationale**: disk I/O bugs are silent until a restore fails; tests are the only early signal.

### VII. Simplicity

- Scope is limited to backing up and restoring USB/SD media as raw or gzip images, and to
  shrinking the last ext4 partition of an image so it fits a smaller medium (by rebuilding the
  file system into a new image; the original image is never modified). Features outside that
  scope (file-level backup, cloud upload, scheduling, other compression formats, growing or
  shrinking other file systems) require a constitution amendment.
- Prefer the simplest design that satisfies the principles above (YAGNI); abstractions MUST be
  justified by a concrete current need.

**Rationale**: a compact tool stays reliable by staying small.

## Technology & Platform Constraints

- **Language**: C#.
- **Runtime**: .NET Framework 4.8 (ships with Windows 10 1903+ and Windows 11, installable on
  Windows 7 SP1 / 8.1). Newer .NET (Core/5+) MUST NOT be used as it does not support Windows 7
  without a large self-contained runtime that violates Principle III.
- **UI**: Windows Forms (built into .NET Framework, Windows 7 compatible, small footprint).
- **Compression**: `System.IO.Compression.GZipStream` from the BCL; external libraries only if
  justified per Principle III.
- **Device access**: P/Invoke to Win32 (`kernel32`, `setupapi`) — no kernel drivers.
- **Localization**: `.resx` resources compiled into the main assembly (no satellite folders in
  the shipped artifact).
- **Build**: SDK-style project targeting `net48`, `AnyCPU` (prefer 32-bit off), producing one
  executable; reproducible build via `dotnet build` / MSBuild.
- **Testing**: an xUnit- or NUnit-based test project that targets `net48`.
- **File systems**: ext4 is read and written by own managed code (no GPL code, no native
  libraries). Linux tools (e.g. `mke2fs`, `e2fsck` in Docker) MAY be used on developer machines
  to create test fixtures and to check outputs independently; the product MUST NOT depend on
  them.

## Development Workflow & Quality Gates

- Work follows Spec Kit: specify → (clarify) → plan → tasks → implement. Each plan MUST pass the
  Constitution Check against Principles I–VII before research and again after design.
- Every change that touches device write paths MUST be reviewed with an explicit Principle I
  checklist.
- A change is mergeable only when: it builds without warnings treated as errors in core
  projects, all automated tests pass, no hard-coded UI strings were introduced, all six
  languages have entries for new strings, and all code/docs are in English.
- Releases MUST record the SHA-256 hash of the shipped executable and the hardware checklist
  results from Principle VI.

## Governance

- This constitution supersedes all other development practices for this project. Where a
  spec, plan, or task conflicts with it, the constitution wins until amended.
- Amendments require: a written proposal (in English) describing the change and its rationale,
  an updated Sync Impact Report at the top of this file, and propagation to dependent
  templates under `.specify/templates/`.
- Versioning follows semantic versioning: MAJOR for removing or redefining a principle, MINOR
  for adding a principle/section or materially expanding guidance, PATCH for clarifications.
- Compliance is reviewed at every plan's Constitution Check and in every code review; any
  justified deviation MUST be recorded in the plan's Complexity Tracking table.

**Version**: 1.1.0 | **Ratified**: 2026-10-15 | **Last Amended**: 2026-10-15
