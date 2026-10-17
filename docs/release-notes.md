# Release notes

Each `## X.Y.Z` section is the text of the GitHub release for that version: the release workflow
copies it verbatim (see [releasing.md](releasing.md)). Keep the heading format
`## X.Y.Z — title (date)`. The authoritative checksums are the ones attached to each GitHub
release.

The earlier local release candidates "1.0.0" and "1.1.0" of 2026-10-15 were never published.
Their contents are part of 1.0.0-rc.1, the first public build.

## 1.0.0-rc.1 — first release candidate (2026-10-17)

First public build of HddBackup: a portable Windows utility for backing up and restoring USB
flash drives and SD/microSD cards. This is a **pre-release**: the manual hardware checklist on
real media has not been completed yet.

### Back up and restore

- Full-device images (every sector, including the partition table and boot sectors) and
  single-partition images with a `.partition.json` metadata sidecar.
- Raw `.img` or gzip-compressed `.img.gz`; optional fast compression. `.img.gz` is written to a
  device as a stream, with no temporary file; multi-member gzip files (for example from `pigz`)
  and gzip files with trailing zero padding are supported.
- SHA-256 computed in the same pass as the transfer: `disk.img.sha256`, and for gzip images also
  `disk.img.gz.sha256`. Checked automatically on restore; optional read-back verification after
  writing and verification of the image file after a backup.
- Progress, current and average speed, elapsed and remaining time, also on the taskbar button.
  100% only after flush, verification, release and eject.
- Safety: only USB, SD and MMC disks are listed; system, boot and page-file disks and the disk
  the program runs from are never offered as targets; every write needs an explicit
  confirmation; volumes are locked and dismounted first. Card readers that misreport the card
  capacity are detected and refused.
- The Restore tab warns as soon as an image is larger than the selected device and offers
  **Shrink and write** when the image can be shrunk to fit.

### Image tab (no device involved)

- **Verify image** against its checksum files, or **Calculate checksums** for an image that has
  none.
- Convert `.img.gz` ↔ `.img`; the result gets checksum files and is verified before it is kept.
- Shrink Linux images (for example Raspberry Pi OS) to the smallest possible size, the size of a
  connected device, or a card size. The last ext4 partition is rebuilt, not resized in place,
  and the result is checked (metadata, every file and attribute, bytes before the partition)
  before it is kept. The original image is never changed.
- Repair after an unclean shutdown: the state of each ext4 partition is shown, and
  **Repair the file system** replays the journal, releases orphan inodes and fixes free space
  counts, as Linux recovery does — as a repaired copy or together with shrinking. Other damage
  is reported and refused.

### Interface

- Two-column tabs: device list on the left, options on the right, path and start button at the
  bottom; explanations in tooltips.
- English, Ukrainian, German, French, Spanish and Japanese, switchable at runtime.
- **Help → Device diagnostics** with device details and a shrink analysis of the selected image.
- Program icon: a blue SD card based on the Papirus icon theme (GPL-3.0).

### Requirements

- Windows 7 SP1, 8.1, 10 or 11 (x86 or x64) with .NET Framework 4.8; administrator rights.
- A single `HddBackup.exe`, no installation. `HddBackup.exe.config` is optional; next to the exe
  it makes Windows 7 and 8.1 offer to install .NET Framework 4.8 when it is missing.
- The executable is not code-signed; SmartScreen may warn the first time it runs.

### Verification status

| Check | Result |
|-------|--------|
| Release build | 0 warnings, 0 errors |
| Automated tests | 349 passed, 2 skipped |
| Independent e2fsprogs check of rebuilt, shrunk and repaired ext4 images (CI job `ext4-check`: `e2fsck -fn`, `sfdisk -V`, `fsck.vfat`, debugfs tree comparison) | Clean and identical |
| Shrinking a 16 GiB `.img.gz` with 3.1 GiB of data to an 8 GiB card size | 4.4 min on a SATA HDD |
| Layout screenshots on Windows 11 at 100% (en, de, fr, ja) | No clipped text |
| Manual hardware checklist on Windows 7 SP1 and Windows 11 (real USB/SD media, boot of a shrunk and of a repaired Raspberry Pi image) | **Pending** |
| 150% and 200% display scaling | **Pending** |

**Use only with media whose contents you can afford to lose until the hardware checklist has
passed.** The program comes with no warranty (GPL-3.0).
