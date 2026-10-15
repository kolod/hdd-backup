# Quickstart & Validation Guide

## Prerequisites

- Windows 10/11 dev machine with .NET SDK (9+), .NET Framework 4.8 targeting pack.
- Test media: one USB flash drive and one SD card (≥ 4 GB, contents expendable), a second
  medium of equal or larger size for restore.
- Test VMs or machines: Windows 7 SP1 (with .NET Framework 4.8 installed) and Windows 11.

## Build & automated tests

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: build succeeds; output folder `src/HddBackup.App/bin/Release/net48/` contains
`HddBackup.exe` (< 5 MB) and **no** culture subfolders or DLLs required at runtime.

### Automated test matrix (must all pass)

| Area | Cases |
|------|-------|
| Round-trip | {Raw, Gzip} × {FullDevice, Partition} × {MBR, GPT} × {512, 4096 sector} — byte-identical |
| Partition restore isolation | other partitions and table bytes unchanged |
| Gzip | multi-member input; > 4 GB logical size (sparse/synthetic stream); corrupt/truncated → `CorruptImage` with offset |
| Safety | oversize image refused; write-protected target refused; image on target refused; HostsExecutable refused; lock failure → zero bytes written; source opened read-only |
| Errors | read/write failure at offset N reports N; device removal mid-run → Failed, `.partial` deleted |
| Cancellation | at random points: backup leaves no final image; restore flushes then reports `ContentComplete=false` |
| Checksums | raw → `disk.img.sha256` only; gz → `disk.img.gz.sha256` + `disk.img.sha256`; values equal to independent SHA-256; no sidecars after cancel/fail; single-byte corruption → `ChecksumMismatch` on restore and verify; one sidecar missing → other still checked; CRLF/upper-case/text-mode sidecars parsed |
| Progress | monotonic; < 100 until Completed; 100 at Completed; ETA within ±15% after 10 s on simulated constant rate |
| Localization | identical key sets in 6 resx; no empty values |

## Manual validation (docs/manual-hardware-checklist.md)

1. Run `HddBackup.exe` from a USB stick on Win7 SP1 and Win11 → UAC prompt, window opens, no
   files created besides optional `HddBackup.ini`.
2. Device list shows only the USB stick/SD card; internal disks absent; the stick the EXE runs
   from is not offered as a restore target.
3. Full backup of SD card to `.img.gz`: progress, speeds, elapsed, remaining update every
   second; at 100% "safe to remove" appears and the card is ejected from Explorer.
   `certutil -hashfile disk.img.gz SHA256` equals `disk.img.gz.sha256`; `7z x` the image and
   `certutil -hashfile disk.img SHA256` equals `disk.img.sha256`. Repeat the backup with
   checksums disabled in a debug build → duration difference ≤ 5% (SC-009).
4. Restore that `.img.gz` to the second medium with verify on; immediately pull the medium when
   100% appears; re-insert and back it up again as `.img` → SHA-256 identical to step 3.
5. Pull the medium mid-restore → error with offset; app stays responsive.
6. Partition backup/restore of partition 2; confirm partition 1 unchanged (hash before/after).
7. Switch through all six languages during idle and after an operation → no untranslated or
   clipped text (check German and Japanese specifically).
8. Throughput: compare `.img` backup time with Win32 Disk Imager on the same medium → ≥ 90%.
