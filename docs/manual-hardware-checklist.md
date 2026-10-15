# Manual hardware checklist

Native device code (enumeration, locking, raw I/O, flushing, eject) cannot be unit-tested
with in-memory devices. Run this checklist before every release on **Windows 7 SP1** (with
.NET Framework 4.8 installed) and on **Windows 11**, as required by constitution Principle VI.

Use only expendable media. Record the results in `docs/release-notes.md`.

## Equipment

- USB flash drive A (≥ 4 GB) with at least two partitions (for example FAT32 + NTFS).
- SD or microSD card B in a USB card reader, and if available in a built-in reader.
- A second medium C that is the same size as or larger than A.
- A USB stick D that holds `HddBackup.exe` and nothing else important.
- Reference tools: `certutil`, 7-Zip, and Win32 Disk Imager (for the throughput comparison).

## Steps

| # | Step | Expected result | Win7 | Win11 |
|---|------|-----------------|------|-------|
| 1 | Run `HddBackup.exe` from stick D | UAC prompt, main window opens, no installer, no files created except `HddBackup.ini` when a setting changes | ☐ | ☐ |
| 2 | Look at the device lists | Only A, B, C, D appear; internal disks never appear; D is missing from the Restore list; a write-protected SD card shows "write-protected" | ☐ | ☐ |
| 3 | Insert and remove a stick while the window is open | Lists refresh within about 1 s; a removed device is deselected | ☐ | ☐ |
| 4 | Full backup of card B to `b.img.gz` | Phase, bar, bytes, current/average speed, elapsed and remaining time update every second; remaining time stabilizes after about 10 s | ☐ | ☐ |
| 5 | When step 4 shows 100% | Green "safe to remove" banner and SHA-256 with a Copy button; the card disappears from Explorer (ejected) | ☐ | ☐ |
| 6 | Check the checksums | `certutil -hashfile b.img.gz SHA256` equals `b.img.gz.sha256`; extract with 7-Zip, then `certutil -hashfile b.img SHA256` equals `b.img.sha256` | ☐ | ☐ |
| 7 | Restore `b.img.gz` to medium C with verify on; **pull C out the moment 100% appears** | No error; after reinserting C, a new raw backup `c.img` has the same SHA-256 as `b.img.sha256` (only the first `b.img` size bytes if C is larger) | ☐ | ☐ |
| 8 | Restore while a file on C is open in Explorer or Notepad | Either the lock succeeds (Explorer releases) or "device is in use" appears **before** anything is written | ☐ | ☐ |
| 9 | Pull C out halfway through a restore | Red banner "device was disconnected" with a byte position; the app stays responsive | ☐ | ☐ |
| 10 | Cancel a backup halfway | Amber banner; no `.img`, `.partial` or `.sha256` file is left behind | ☐ | ☐ |
| 11 | Cancel a restore halfway | Confirmation that warns about incomplete data; afterwards "can be removed" and "incomplete data" | ☐ | ☐ |
| 12 | Back up partition 2 of A, then restore it into partition 2 of C (same layout) | `.partition.json` exists; partition 1 on C is unchanged (hash its volume before and after) | ☐ | ☐ |
| 13 | Set the lock switch on card B and try to restore to it | Refused before writing: "write-protected" | ☐ | ☐ |
| 14 | Try to save an image onto the device being backed up | Refused: "cannot be saved on the device being backed up" | ☐ | ☐ |
| 15 | Switch through all six languages on both tabs, the confirmation dialog, and a result banner | Every text is translated; nothing is clipped (check German and Japanese especially) | ☐ | ☐ |
| 16 | Throughput: raw backup of A, then the same with Win32 Disk Imager | HddBackup takes at most 1.11× as long (≥ 90% of the reference speed, SC-003) | ☐ | ☐ |
| 17 | Checksum overhead: `.img` backup of A vs. the same read without hashing (Win32 Disk Imager "Read Only Allocated" off) | Duration difference ≤ 5% (SC-009) | ☐ | ☐ |
| 18 | Run the program from a write-protected stick | Program works; settings are kept for the session only, no error | ☐ | ☐ |
