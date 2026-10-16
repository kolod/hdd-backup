# HddBackup

[![CI](https://github.com/kolod/hdd-backup/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/kolod/hdd-backup/actions/workflows/ci.yml)

A compact, portable Windows utility for backing up and restoring USB flash drives and
SD/microSD cards.

- Full-device images: every sector, including the partition table (MBR/GPT) and boot sectors.
- Single-partition images, with a `.partition.json` metadata sidecar.
- Raw `*.img` or gzip-compressed `*.img.gz` images; `.img.gz` is written to a device as a stream,
  with no temporary uncompressed file.
- SHA-256 checksums are computed in the same pass and saved next to the image
  (`disk.img.sha256`; for gzip images also `disk.img.gz.sha256`).
- Progress, current and average speed, elapsed and remaining time. Progress reaches 100% only
  when the device can be removed safely.
- Shrinking Linux images (for example Raspberry Pi OS) so they fit a smaller card: the last
  ext4 partition is rebuilt at a smaller size in a new image file, natively on Windows. On the
  **Image** tab choose the smallest possible size, the size of a connected device, or a card
  size, then **Create smaller image**. The Restore tab warns when an image is larger than the
  selected card and offers "Shrink and write". The original image is never changed.
- The **Image** tab also verifies an image against its checksum files without touching a
  device.
- UI languages: English, Ukrainian, German, French, Spanish, Japanese.

## Disclaimer

HddBackup writes directly to disks. Restoring an image overwrites **all** data on the target
device, and a wrong choice of device cannot be undone. Check the selected device before every
write and keep copies of important data elsewhere.

The software is provided "as is", without warranty of any kind. The author is not liable for
any loss of data, hardware damage, or financial or other losses arising from its use. See
sections 15–16 of the [LICENSE](LICENSE).

## Requirements

- Windows 7 SP1, 8.1, 10 or 11 (x86 or x64).
- .NET Framework 4.8 (preinstalled on Windows 10 1903+ and Windows 11; on Windows 7 SP1 and
  8.1 it must be installed once).
- Administrator rights (the program asks for elevation at start).

The program is a single `HddBackup.exe`. It needs no installation and writes nothing to the
registry. Settings are kept in an optional `HddBackup.ini` next to the executable.

## Build and test

```powershell
dotnet build HddBackup.sln -c Release -warnaserror
dotnet test  tests/HddBackup.Core.Tests -c Release
Invoke-Pester tools/tests            # scripts used by CI and releases (Pester 5)
```

The executable is produced at `src/HddBackup.App/bin/Release/net48/HddBackup.exe`.

Every pull request and push to `main` runs the same build and tests on GitHub Actions (`CI`),
plus an independent check of the ext4 images produced by the tests with the Linux `e2fsck`.
Changes reach `main` only through pull requests with green checks. How releases are made:
[docs/releasing.md](docs/releasing.md).

## Releases

Download `HddBackup.exe` from the
[GitHub Releases page](https://github.com/kolod/hdd-backup/releases). Every release lists the
SHA-256 of its files in the release text and in `SHA256SUMS`; check your download with:

```powershell
Get-FileHash .\HddBackup.exe          # or: certutil -hashfile HddBackup.exe SHA256
```

Releases are published only after the manual hardware checklist has passed, and published
releases cannot be changed afterwards. The executable is not code-signed, so Windows SmartScreen
may warn about an unrecognized app the first time it runs.

## Safety

Only USB, SD and MMC disks are listed. Disks that hold Windows, the boot partition or a page
file are never shown. The disk the program itself runs from is never offered as a write target.
Every write needs an explicit confirmation, and every volume on the target is locked and
dismounted first.

## Known limitations

Some old built-in card readers report a wrong capacity for SDHC/SDXC cards. For example, a
Ricoh R5C843 with the `rimmptsk` driver on Windows 7 reports a 16 GB card as 256 MB. Windows
then refuses to read past the reported size, and that driver does not support SCSI
pass-through, so no program can read the rest of the card through it. HddBackup detects
partitions that extend past the reported size. It shows such a device greyed out as an
"unsuitable reader" and refuses every backup and restore through it. For comparison purposes
a backup can still be forced with "Back up anyway (incomplete image)". The result is then marked
as incomplete. Restoring through such a reader is never allowed. Use a USB card reader for
such cards. **Help → Device diagnostics…** shows the details.

This check relies on the partition table. A reader that misreports the capacity of a card
whose partitions all fit within the reported size cannot be detected this way. For such cards,
compare the SHA-256 of images made with two different readers.

### Shrinking images

Shrinking works on full-device images with an MBR or GPT table whose **last** partition holds
ext4, as in Raspberry Pi OS and most single-board-computer images. Partitions before it (such as
the FAT32 boot partition) are copied byte for byte. The ext4 file system is rebuilt rather than
resized in place, and the result is checked before it is kept: a full metadata check, a
comparison of every file, directory, link, owner, permission and extended attribute with the
source, and a byte comparison of everything before the root partition.

Supported ext4 features: `has_journal`, `ext_attr`, `resize_inode`, `dir_index`, `filetype`,
`extent`, `64bit`, `flex_bg`, `metadata_csum` (+ `metadata_csum_seed`), `sparse_super`,
`large_file`, `huge_file`, `uninit_bg`, `dir_nlink`, `extra_isize`; 1, 2 or 4 KiB blocks;
128- or 256-byte inodes.

Images are refused, with the reason shown, when:
- the file system was not shut down cleanly or its journal needs recovery (start the device
  once, shut it down properly, and make a new backup);
- the file system has recorded errors or unfinished deletions (orphan inodes);
- it uses other features, such as `inline_data`, `bigalloc`, `encrypt`, `casefold` or
  `quota`;
- ext4 is not the last partition, or there are extended (logical) partitions;
- the data would not fit even at the smallest possible size.

The smallest result is the used data plus a journal and free space of 5% of the data or
128 MiB, whichever is larger (less for very small images). The shrink needs free space in the output folder for the result
and, for an `.img.gz` source, for a temporary uncompressed copy. The temporary copy is deleted
afterwards.

Hardware validation steps: [docs/manual-hardware-checklist.md](docs/manual-hardware-checklist.md).
Design documents: [specs/001-usb-sd-imaging/](specs/001-usb-sd-imaging/),
[specs/002-ext4-image-shrink/](specs/002-ext4-image-shrink/).

## License

Copyright © 2026 Oleksandr Kolodkin.

HddBackup is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of
the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY.
See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
