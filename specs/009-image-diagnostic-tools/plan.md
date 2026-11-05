# Implementation Plan: Diagnostic Tools for the Opened Linux File System (SSH Check, Startup and Scheduled Tasks)

**Branch**: `009-image-diagnostic-tools` | **Date**: 2026-11-05 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/009-image-diagnostic-tools/spec.md`

## Summary

A **Tools** button on the Files tab (feature 008) opens two read-only tools about the Linux file
system that is open there. Both are built on one small layer, `SystemReader`, that reads files and
directories of the opened tree and resolves symbolic links **inside the image** (never on the host).
On top of it, own parsers read the OpenSSH server configuration, the account databases, systemd
units, cron files and desktop autostart entries. `SshCheck` turns them into findings and a verdict;
`StartupInventory` turns them into one list of startup entries and cron tasks. Core returns
language-neutral results (codes and arguments); the App formats them with the resource tables, the
schedule words included. Each tool opens its own read-only source on a worker thread, like an
extraction, so the tree stays responsive. Secrets (password hashes, key text) are reduced to facts
at the moment they are parsed and never stored in a result.

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: BCL and Windows Forms only; feature 008's `IFileTree`, `BrowseSource`,
`Ext4FileSystemView`; no new packages, no GPL code, no regex-heavy third-party parsers

**Storage**: none; the only output files are reports the user saves to a folder of their choice

**Testing**: xUnit with an in-memory Linux root builder (fast, the SC-001 matrix), a few small real
ext4 fixtures generated in Docker, and an independent check in the `ext4-check` job: `sshd -T` for
the effective SSH settings and `systemctl --root` for the enabled units (R-12)

**Target Platform**: Windows 7 SP1 – Windows 11, x86/x64

**Project Type**: desktop app (single WinForms executable; Core compiled in)

**Performance Goals**: result within 5 s for an image or device of up to 32 GiB (SC-003), because
only a few hundred small files and directories are read; one analysis never reads file contents
larger than 1 MiB

**Constraints**: read-only; no secret value in any result, window, clipboard text or saved file;
no network access; no new runtime dependency; executable stays under 5 MB

**Scale/Scope**: ~14 Core classes (~1 800 lines), 2 windows and a button, ~140 strings × 6
languages (findings, verdicts, schedule words), ~16 small fixtures, constitution already amended
(1.4.0)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ Read-only on the image and the device; no write path; secrets never stored or shown | ✅ Secrets are reduced to facts inside the parsers (R-6) and a final guard test scans every result for planted secrets; the tools use their own read-only source (R-2) |
| II | Faithful Imaging | ✅ Images are not modified | ✅ SHA-256 of the image unchanged in tests (SC-005) |
| III | Portable & Compact | ✅ No dependencies | ✅ Own small parsers and a wildcard matcher instead of libraries; no new resources except strings |
| IV | Win 7 – 11 | ✅ Managed code only | ✅ `ListView`, `ContextMenuStrip`, `SaveFileDialog`; no newer controls |
| V | Localized UI | ✅ New strings in six languages | ✅ Core returns codes; all texts and schedule words come from resources (R-9); string list in contracts/ui.md |
| VI | Test-Verified Disk I/O | ✅ Core logic behind `IFileTree`; fakes exist | ✅ Matrix of ≥ 12 SSH cases, ≥ 40 startup/cron entries, secret-leak test, independent oracles in CI |
| VII | Simplicity — scope | ✅ Amendment 1.4.0 (applied 2026-11-05) covers read-only diagnostics | ✅ No write path, no fixing, no network, no second file system reader; FAT boot partition stays out (Q1) |

No violations; the Complexity Tracking table is empty.

## Project Structure

### Documentation (this feature)

```text
specs/009-image-diagnostic-tools/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/ui.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
src/HddBackup.Core/Diagnostics/                 # NEW
├── SystemReader.cs        # reads files/dirs of an IFileTree; symlinks resolved inside the image; caches
├── WildcardMatcher.cs     # * ? [] matching for Include and drop-in directories
├── SshdConfig.cs          # parser (Include, Match, first wins, keywords) → effective settings
├── AccountDatabase.cs     # passwd + shadow + shells → Account (facts only); authorized keys counted
├── SshCheck.cs            # findings + verdict from the above
├── SshReport.cs           # Finding, FindingStatus, Verdict, Account, evidence (language-neutral)
├── SystemdScan.cs         # units, enabled links, masked, default target reachability, timers, user units
├── CronScan.cs            # crontabs, cron.d, periodic dirs, anacrontab, @reboot
├── CronSchedule.cs        # parsed schedule (fields or shortcut) + classification for words
├── AutostartScan.cs       # rc.local, init.d links, XDG .desktop, lxsession/labwc autostart
├── StartupInventory.cs    # merges the scans into StartupEntry / ScheduledTask lists
├── ReportBuilder.cs       # text report from a result with a callback for texts (no secrets by construction)
└── DiagnosticsRunner.cs   # opens its own read-only source + partition, runs a tool, disposes

src/HddBackup.App/Forms/
├── FilesTab.cs            # Tools button (+ menu), enabling rules, starts the tools
├── SshWizardDialog.cs     # NEW: four pages
├── StartupTasksWindow.cs  # NEW: list, filter, search, copy/save
├── DiagnosticTexts.cs     # NEW: findings/verdicts/entries → localized text; schedule words
└── (ExtractSummaryDialog.cs pattern reused for copy/save)
src/HddBackup.App/Resources/Strings*.resx        # ~140 strings × 6

tests/HddBackup.Core.Tests/Diagnostics/          # SshdConfigTests, AccountDatabaseTests, SshCheckMatrixTests,
                                                 # SystemdScanTests, CronScanTests, CronScheduleTests,
                                                 # AutostartScanTests, StartupInventoryTests, SecretLeakTests,
                                                 # ReadOnlyTests, SystemReaderTests, FixtureTests
tests/HddBackup.Core.Tests/Fakes/LinuxRootBuilder.cs   # builds an in-memory Linux root (files, links, modes)
tests/fixtures/ext4/make-diagnostic-fixtures.sh        # small real roots + expected results from sshd -T / systemctl --root
tests/fixtures/ext4/check-diagnostics.sh               # independent oracles: sshd -T, systemctl --root (R-12)
tools/ci/Invoke-Ext4Checks.sh                          # calls check-diagnostics.sh for the exported listings
docs/manual-hardware-checklist.md, README.md, docs/release-notes.md, docs/safety-review.md
```

**Structure Decision**: a new Core namespace `Diagnostics` next to `Browsing`; one shared reader
(`SystemReader`) so every parser sees the same resolved, cached view of the image; the App only
formats and displays. The tools are not engine operations: they have no destination and finish in
seconds, so they use a simple "analysing…" state with Cancel instead of the progress panel.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| (none) | | |
