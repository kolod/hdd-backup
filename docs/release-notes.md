# Release notes

## 1.0.0 — release candidate (2026-10-15)

First implementation of feature `001-usb-sd-imaging`.

### Contents

- Full-device and single-partition backup to `.img` / `.img.gz`.
- Restore from `.img` and `.img.gz` (streamed, with no temporary file), including multi-member
  gzip files (for example from `pigz`) and gzip files with trailing zero padding.
- SHA-256 computed in the same pass as the transfer: `disk.img.sha256`, and for gzip images
  also `disk.img.gz.sha256`. Checked automatically on restore; separate "Verify image" action.
- Progress, current and average speed, elapsed and remaining time; 100% only after flush,
  verification, release and eject; progress also shown on the taskbar button.
- UI in English, Ukrainian, German, French, Spanish and Japanese, switchable at runtime.

### Artifact

| File | Size | SHA-256 |
|------|------|---------|
| `HddBackup.exe` (Release, AnyCPU, .NET Framework 4.8) | 221,696 bytes | `b1b9801fcc35483ec77b73c07df5b549293fd338f6553622cf40eff6021376b8` |

The build also produces `HddBackup.exe.config` and `HddBackup.pdb`. Neither is needed to run the
program. The `.config` file makes Windows offer to install .NET Framework 4.8 when it is missing.
Shipping it next to the exe is recommended for Windows 7 and 8.1 users.

### Verification status

| Check | Result |
|-------|--------|
| `dotnet build -c Release` | 0 warnings, 0 errors |
| Automated tests (`dotnet test`) | 133 passed, 0 failed (3 consecutive runs; includes a gzip stream over 4 GiB) |
| `tests/check-artifact.ps1` | Passed: single exe < 5 MB, 6 embedded languages, no satellites or DLLs, requireAdministrator |
| GUI launch (en, uk, ja) on Windows 11 | Window opens with a localized title and exits cleanly |
| Layout screenshots (en, de, fr, ja) | No clipped text on the Back up and Restore tabs |
| Read-only device enumeration on the dev machine | System NVMe disk detected as protected; no internal disk listed |
| Manual hardware checklist on Windows 7 SP1 | **Pending** (needs real USB/SD media) |
| Manual hardware checklist on Windows 11 | **Pending** (needs real USB/SD media) |
| Throughput vs. reference tool (SC-003), checksum overhead (SC-009) | **Pending** (needs real media) |

Do not publish this build until the pending rows of the hardware checklist pass.
