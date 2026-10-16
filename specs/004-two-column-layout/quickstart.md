# Quickstart: Validating the Two-Column Layout and the Image Tab

## Prerequisites

- Windows 10/11 developer machine with the .NET SDK; a Windows 7 SP1 machine or VM for the
  wrapping check.
- One or two USB flash drives or SD cards; a Raspberry Pi OS-like full-device image
  (`tests/HddBackup.Core.Tests/Fixtures/ext4/rpi-like-4k.img.gz` works) and a partition image
  made by the app.

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: build with no warnings; all tests pass, including the new `ImageFit`,
`ShrinkOutputName` tests and the resource parity test with the new keys.

Baseline (2026-10-15, before this feature): 233 tests pass. At the default window size
(820 × 680, English, 100%, Windows 11) the device list is about 395 px high on the Backup tab and
about 428 px on the Restore tab.

## Manual scenarios

Run `src/HddBackup.App/bin/Release/net48/HddBackup.exe` (elevated).

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Open Backup at default size | Device list left, Backup scope / Format / After backup right, path and **Start backup** at the bottom | US1, US2 |
| 2 | Compare device list height with the previous release | At least 25% more visible rows | SC-001 |
| 3 | Switch to Single partition | Partition list appears under the device list on the left | US1 |
| 4 | Hover Whole device, format list, Fast compression, "Offset" header | Tooltips from the tooltip map | US6 |
| 5 | Run a backup and a restore | Same steps and dialogs as before; progress panel unchanged | SC-005 |
| 6 | Select an image on Restore, open Image | Same path, description, checksum files | US4 |
| 7 | Image tab with the rpi-like image | Analysis shows partitions, used data, smallest size; **Create smaller image** enabled | US3 |
| 8 | Size of a device → pick a connected card → Create smaller image | New file sized for that card; nothing written to the card; result panel shows the path | US3, FR-011 |
| 9 | Card size below the smallest size | Create disabled, reason shown | Edge case |
| 10 | Image tab with a partition image | Shrink controls disabled with a reason; **Verify image** works | US3 |
| 11 | Verify an image without checksum files, save them | Both tabs show the new checksum files | FR-017 |
| 12 | Restore: 16 GB image, 8 GB card | Warning under the description; **Write to device…** shows "Shrink and write" | US5 |
| 13 | Unplug the device chosen under "Size of a device" | Choice falls back to Smallest possible | Edge case |
| 14 | Change the image while "Analyzing…" | Old analysis discarded, new one shown | Edge case |

## Layout matrix (SC-002)

Take a screenshot of each tab in each language (English, Ukrainian, German, French, Spanish,
Japanese) at 100% and at 150% scaling on Windows 11, and German and Japanese at 100% on
Windows 7. Check: no clipped labels in the right columns, long check-box texts wrap, the device
name and size columns are visible at the minimum window size, the window fits a 1024×768 screen.
Record the results in the table below.

| Date | OS / scaling | Languages | Result |
|------|--------------|-----------|--------|
| 2026-10-15 | Windows 11, 100%, 980 × 680 | en, uk, de, fr, es, ja (all three tabs, image loaded) | No clipped labels; right column 220–300 px, no text needed wrapping |
| 2026-10-15 | Windows 11, 100%, minimum 800 × 560 | de, ja (Backup, Image) | Device and size columns visible (list scrolls horizontally); right column not clipped |
| — | Windows 11, 150% / 200% | — | Not run yet |
| — | Windows 7 SP1 | — | Not run yet (wrapping fallback of research R-1 unverified) |

## Results (2026-10-15)

- Build: 0 warnings. Tests: 251 passed (233 baseline + 9 `ShrinkOutputName` + 9 `ImageFit`).
- In-process UI check (Windows PowerShell 5.1 harness loading the built executable): tooltips
  present and parenthesis-free labels in all six languages; Image tab analysis of
  `rpi-like-4k.img.gz` (smallest 243 MiB), Create disabled with the reason for a 100 MiB card,
  enabled for 1 GiB and for an 8 GiB device (`_shrunk_8G.img[.gz]`), request
  `ShrinkImage` / target 8 GiB / `EjectAfter = false`, Verify request `VerifyImage`, fallback to
  "Smallest possible" when the chosen device disappears, shared image shown on the Restore
  tab, Restore warning for a 100 MB device and none for an 8 GiB device.
- SC-001 (device list height, English, default size, no devices): Backup 395 → 475 px (+20%);
  Restore 428 → about 457 px with a one-line image description (+7%). **Not met** (target 25%):
  on the Restore tab only the two check boxes moved out of the left column.
- Not run: header tooltip hover on a real partition list (needs a device), scenarios on real
  media (5, 8 with a real card, 12, 13), 150% scaling and Windows 7.
