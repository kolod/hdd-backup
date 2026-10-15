# HddBackup

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
- UI languages: English, Ukrainian, German, French, Spanish, Japanese.

## Requirements

- Windows 7 SP1, 8.1, 10 or 11 (x86 or x64).
- .NET Framework 4.8 (preinstalled on Windows 10 1903+ and Windows 11; on Windows 7 SP1 and
  8.1 it must be installed once).
- Administrator rights (the program asks for elevation at start).

The program is a single `HddBackup.exe`. It needs no installation and writes nothing to the
registry. Settings are kept in an optional `HddBackup.ini` next to the executable.

## Build and test

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release
```

The executable is produced at `src/HddBackup.App/bin/Release/net48/HddBackup.exe`.

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

Hardware validation steps: [docs/manual-hardware-checklist.md](docs/manual-hardware-checklist.md).
Design documents: [specs/001-usb-sd-imaging/](specs/001-usb-sd-imaging/).
