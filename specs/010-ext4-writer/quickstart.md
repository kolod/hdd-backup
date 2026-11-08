# Quickstart: Validating File Editing

## Fixtures (developer machine with Docker)

```bash
docker run --rm -v "$PWD/tests/fixtures/ext4:/w/scripts:ro" \
           -v "$PWD/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
           debian:bookworm bash /w/scripts/make-editing-fixtures.sh
```

Produces real ext4 images: `edit-basic` (htree directory of 600 files, hard links, xattr block,
symlink, empty and large file), `edit-1k` (1 KiB blocks), `edit-nocsum` (group checksums only),
`edit-full` (a few free blocks), `edit-uninit` (many uninitialised groups). The existing
`rpi-like-4k` (MBR: FAT + ext4) is used for the card tests.

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release --filter "FullyQualifiedName~Editing"
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all pass, among them: allocator (every group state), directory editor (insert,
remove, rename, htree → linear, new block, tails), editor (grow/shrink across block boundaries, new
file/dir, delete with hard links and nested directories, xattr), diff file (round trip, atomicity,
identity change), pending tree, image session (nothing written before Save: SHA-256 equal; Save equals
the expected tree; checksum file updated or created), interruption at every write (finish equals the
uninterrupted image), card session (zero writes outside the partition, FAT bytes equal, no files
created), refusal tests (compressed, unclean, unsupported feature, no space, changed image), the own
checker on every result, resource parity, and the browse-mode regression (features 008/009 suites).

## Independent check (Docker, also the `ext4-check` CI job)

```bash
bash tests/fixtures/ext4/check-editing.sh <exports-dir> <fixtures-dir>
```

Expected: for each exported `saved-<scenario>.img`, `e2fsck -fn` reports no error and the
`debugfs rdump` tree equals the expected listing exported by the test (R-14).

## Manual scenarios

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Files tab → image → **Edit** → select `/etc/hostname` → change text → **Save** → confirm | Dialog lists the change and the checksum file; after the save the tree and viewer show the new text; `certutil -hashfile` equals the `.sha256` | US1, US4 |
| 2 | Make three changes, close the program, start it, open the image | Edit mode comes back with "3 changes not saved"; the image's hash is unchanged | US2 |
| 3 | **Discard** | Diff file gone, image as before | US2 |
| 4 | New file, new folder, rename, delete a folder with content → Save | Tree matches; `e2fsck -fn` (Linux) clean | US3 |
| 5 | Image without `.sha256` → Save | `<image>.sha256` created; **Verify image** passes | US4 |
| 6 | Kill the program during Save (Task Manager) → start → open the image | "A save was interrupted" → **Finish save** → same result as uninterrupted | US6 |
| 7 | Card in the reader → **Edit** → confirmation names the device → edit a file → **Save** | Written at once; FAT partition untouched (hash of its range equal); no diff or checksum file; Pi boots with the change | US5 |
| 8 | Card: pull it out during a change | Stops with cause and offset, leaves edit mode | US5 |
| 9 | Compressed `.img.gz`, an image needing recovery, a write-protected card | **Edit** refused with the reason | edge cases |
| 10 | Switch the language to uk, de, fr, es, ja with edit mode on | Texts change; no clipping | FR-024 |
| 11 | Windows 7 SP1 and Windows 11: scenarios 1–8 on real hardware | Pass; writing to the ext4 partition of a card works while the FAT volume stays mounted | FR-025 |

Scenarios 7, 8 and 11 join `docs/manual-hardware-checklist.md`.
