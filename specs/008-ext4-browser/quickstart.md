# Quickstart: Validating the Files Tab

## Fixtures (developer machine with Docker)

```bash
docker run --rm -v "$PWD:/w" -w /w debian:bookworm bash tests/fixtures/ext4/make-browse-fixtures.sh
```

Produces, next to the existing fixtures: `browse-names` (spaces, UTF-8, invalid UTF-8 bytes, `:`
and reserved names, case collisions, a 300-character path, symlinks, FIFO, socket, hard links),
`browse-large` (sparse file, 2 GiB hole file, a directory with 20 000 entries) as `.img` files,
each with a manifest of its tree. The repair fixtures of feature 006 (`dirty-journal`, `orphans`,
`not-clean`, `damaged-dirent`) are reused for User Story 5.

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release --filter "FullyQualifiedName~Browsing"
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all tests pass, including source opening (image, single-partition image,
device fake, `.img.gz` refusal, no ext4), tree and counts, state classification (shared with the
shrink planner, whose tests are unchanged), preview (text, binary, truncated, empty), name mapping,
planner (conflicts, space, FAT limit, destination guards), extraction (tree equals the manifest,
cancel at each phase, one failing item continues) and the read-only tests (zero writes, image hash
unchanged).

## Independent check (Docker, also the `ext4-check` CI job)

```bash
bash tests/fixtures/ext4/check-output.sh --extracted <extracted-dir> <fixture.img>
```

Expected: the tree and every file extracted by the tests equal `debugfs -R "rdump / <dir>"` of the
source image, apart from the names Windows cannot store (listed in the test's expected renames).

## Manual scenarios

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Files tab, choose `browse-names.img` | Partition selected automatically, state "Clean", root shown within 5 s, counts in the status line | US1, FR-004, FR-008, SC-001 |
| 2 | Expand `/etc`, click `hostname` | Header with path, size, mode, owner, time; text shown | US3, FR-009, FR-010 |
| 3 | Click a binary (`/bin/ls`) | Hex dump with offsets; Text radio shows the same bytes as text | FR-010 |
| 4 | Click a 2 GiB file | First 1 MiB (text) or 256 KiB (hex) and "Showing the first … of 2 GiB" | FR-010 |
| 5 | Click a symlink, a FIFO, a directory | Header only; link target shown; no content | FR-011 |
| 6 | Select `/etc`, **Extract…** to an empty folder | Progress, summary with the symlinks skipped; files equal the source (SHA-256) | US2, FR-012–FR-018 |
| 7 | Extract the same directory again | "N files already exist": Replace / Skip existing / Cancel; Cancel changes nothing | FR-015 |
| 8 | Open the 20 000-entry directory | Expands in under 2 s on Windows 7 and 11 | SC-002 |
| 9 | Extract a 2 GiB file to a FAT32 USB stick | Refused before writing for that item (FAT limit) in the summary | edge cases |
| 10 | Extract with the destination on a nearly full disk | Refused before writing with needed and free sizes | FR-016 |
| 11 | Start a large extraction, press Cancel | Stops within 2 s; no `.hddbackup-part-*` file and no truncated file left | FR-014, SC-005 |
| 12 | Choose the image's own folder and a file named like the image | Refused (destination is the image) | FR-019 |
| 13 | Pick `dirty-journal.img` | Warning about unclean unmount with the Image-tab pointer; files browsable; image hash unchanged | US5, FR-020 |
| 14 | Pick `damaged-dirent.img`, select the damaged directory | Marked unreadable with the reason; siblings usable; extraction lists it and continues | FR-021 |
| 15 | Select a `.img.gz` | Conversion hint, no file created | FR-002 |
| 16 | **Device**: real SD card with a Raspberry Pi image | Same tree as the image of that card; no write dialog; card unchanged (compare its image hash before and after) | US4, SC-004 |
| 17 | Remove the card while browsing | Tree cleared, error with cause | US4 |
| 18 | Switch the language to uk, de, fr, es, ja | All tab texts translated, no clipping at 100% | FR-023, SC-007 |
| 19 | Windows 7 SP1 and Windows 11 on a real card and image | Scenarios 1–6, 11, 16 pass | FR-024; manual hardware checklist |

Scenarios 16, 17 and 19 join `docs/manual-hardware-checklist.md`.
