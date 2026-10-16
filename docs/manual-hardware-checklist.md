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

## Shrinking ext4 images (feature 002)

Equipment: a Raspberry Pi with a 16 GB Raspberry Pi OS card E (booted at least once, shut down
properly) and an 8 GB card F. Back up E to `pi.img.gz` first (step 4).

| # | Step | Expected result | Win7 | Win11 |
|---|------|-----------------|------|-------|
| 19 | Restore tab → `pi.img.gz` → select F (the warning "will be shrunk to fit before writing" appears under the image description) → **Write to device…** | Offer dialog: image size, card size, used data, result size, output path; the original is named as unchanged | ☐ | ☐ |
| 20 | **Shrink and write** → confirm the write dialog | "Step 1 of 2" shrink phases, then "Step 2 of 2" write; final banner names the saved smaller image and "safe to remove". Time the shrink step: ≤ 15 min on an SSD (SC-004) | ☐ | ☐ |
| 21 | Boot the Pi from F | Boots to the desktop/login; `sudo apt update` works; `df -h /` shows the smaller root; `sudo touch /forcefsck` + reboot shows no fsck errors | ☐ | ☐ |
| 22 | Check `pi_shrunk_*.img` from step 20 | `certutil -hashfile` equals its `.sha256`; `pi.img.gz` and its sidecars are unchanged (hash before/after) | ☐ | ☐ |
| 23 | Pull card E out of the Pi without shutting down, back it up again, then open that backup on the **Image** tab | Shrink analysis shows "journal needs recovery" (or "not cleanly unmounted") with advice; **Create smaller image** is unavailable | ☐ | ☐ |
| 24 | **Image** tab → Smallest possible, output format Compressed → **Create smaller image** | Result ≤ used data + 10 % (or + 256 MB) (SC-005); writing it to F and booting works as in step 21 | ☐ | ☐ |
| 25 | Cancel a shrink halfway | Amber banner "no image file was created"; no `.partial` or `.work` file left next to the output | ☐ | ☐ |
| 26 | Shrink with too little free space on the output drive | Refused at the start with "not enough free space"; nothing left behind | ☐ | ☐ |
| 27 | **Image** tab → `pi.img.gz` → Size of a device → card F → **Create smaller image** | `pi_shrunk_<size>G.img` fits F; nothing is written to F (its volumes stay mounted, contents unchanged) | ☐ | ☐ |
