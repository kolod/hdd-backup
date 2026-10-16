# Release notes

Each `## X.Y.Z` section is the text of the GitHub release for that version: the release workflow
copies it verbatim (see [releasing.md](releasing.md)). Keep the heading format
`## X.Y.Z — title (date)`; the hashes listed under "Artifact" in older sections are from local
builds, while the authoritative checksums are the ones attached to each GitHub release.

## Unreleased — feature `004-two-column-layout`

- Back up and Restore tabs: device list on the left, options grouped in a right column, image
  path and the start button at the bottom.
- New **Image** tab for work that does not touch a device: **Verify image** and **Create smaller
  image** (smallest possible, size of a connected device, or a card size). It replaces the
  Shrink image dialog; the Restore tab keeps only **Write to device…**.
- The Restore and Image tabs share the selected image.
- The Restore tab warns as soon as the image is larger than the selected device, and says
  whether it can be shrunk to fit.
- Explanations formerly in parentheses (whole device, formats, fast compression, back up anyway,
  offset) are now tooltips.
- Default window width 980 px (was 820 px).
- Progress of writing and verifying `.img.gz` images follows the uncompressed data, using an
  estimate of the uncompressed size for whole-device images; before, it followed the compressed
  file only and could stay near 0% for a long time on images with a mostly empty start.
- Image tab (feature `005-image-conversion`): convert `.img.gz` ↔ `.img` ("Same as the image" size
  with the other format; checksum files and partition information are written, the source is
  checked against its checksums and the result is verified before it is kept); **Calculate
  checksums** saves missing checksum files in one pass (replaces the question after verifying an
  image without checksums); fast compression for compressed output; **Create smaller image** is
  now **Create image**.

## 1.1.0 — release candidate (2026-10-15)

Feature `002-ext4-image-shrink`: fit a Linux (for example Raspberry Pi OS) image onto a smaller
card, natively on Windows.

### Contents

- **Shrink image…** on the Restore tab: smallest possible size or a given card size; `.img` or
  `.img.gz` output with SHA-256 sidecars.
- **Shrink and write**: offered when an image is larger than the selected card; the smaller
  copy is saved next to the original and then written through the normal confirmation.
- The last ext4 partition is rebuilt (not resized in place) and verified before it is kept.
  The original image is never modified. Unsafe images are refused with a specific, translated
  reason (journal needs recovery, not clean, errors, orphans, unsupported features or layout).
- Help → Device diagnostics now includes a shrink analysis of the selected image.

### Artifact

| File | Size | SHA-256 |
|------|------|---------|
| `HddBackup.exe` (Release, AnyCPU, .NET Framework 4.8) | 373,760 bytes | `a53157cb46c20c94421b28d5f7dc10f937b1b2a24f09120a6253506da5318e98` |

### Verification status

| Check | Result |
|-------|--------|
| `dotnet build -c Release` | 0 warnings, 0 errors |
| Automated tests (`dotnet test`) | 233 passed, 0 failed |
| `tests/check-artifact.ps1` | Passed (single exe < 5 MB) |
| Independent e2fsprogs 1.47.0 check of rebuilt and shrunk fixtures (`e2fsck -fn`, `sfdisk -V`, `fsck.vfat`, debugfs tree comparison) | All clean and identical (see `specs/002-ext4-image-shrink/quickstart.md`) |
| SC-004: 16 GiB `.img.gz` with 3.1 GiB data → 8 GiB card size | 4.4 min on a SATA HDD (target ≤ 15 min on an SSD): unpack 164 s, rebuild 66 s, verify 3 s (data still in the file cache), save < 1 min. Result independently checked: `e2fsck -fn` clean, 33,174 objects identical to the source |
| Shrink dialogs in en/de/ja | No clipped text |
| Raspberry Pi boot from a shrunk image (checklist steps 19–26) | **Pending** (needs real hardware) |
| Manual hardware checklist steps 1–18 on Windows 7 SP1 / Windows 11 | **Pending**, as for 1.0.0 |

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
