# Quickstart: Validating ext4 Repair

## Fixtures (developer machine with Docker)

```bash
docker run --rm -v "$PWD:/w" -w /w debian:bookworm bash tests/fixtures/ext4/make-fixtures.sh
```

Produces, next to the existing fixtures: `dirty-journal`, `dirty-journal-torn`, `orphans`,
`not-clean`, `damaged-dirent` images (`.img.gz`) and, for the first four, the manifest of the tree
after `e2fsck -fy` (the Linux reference).

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all tests pass, including journal parser tests (64-bit tags, checksums v3,
revoke, escape, torn transaction), overlay tests, orphan handling, finding classification, repair
at the same size and repair + shrink for each fixture (tree equals the reference manifest; own
full check clean), refusal of `damaged-dirent`, cancellation leaving no files.

## Independent check (Docker, also the `ext4-check` CI job)

```bash
bash tests/fixtures/ext4/check-output.sh <repaired.img> <reference-after-e2fsck.img>
```

Expected: `e2fsck -fn` exits 0 with no messages; trees identical apart from inode numbers.

## Manual scenarios

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Image tab, `dirty-journal.img.gz` | File system: "needs repair — the journal needs recovery"; Repair checked; output `dirty-journal_repaired.img` | FR-001, FR-002 |
| 2 | Create image (Same as the image) | "Replaying the journal…", rebuild, verify; result names the file and what was repaired | US1 |
| 3 | Open the result | File system: clean; Repair hidden | US1 |
| 4 | `orphans.img.gz` → Smallest possible | One result, repaired and shrunk | US3 |
| 5 | Uncheck Repair with a shrink size | Create unavailable with the reason | contracts/ui.md |
| 6 | `damaged-dirent.img.gz` | Red "cannot be repaired" line; Repair hidden; shrink unavailable | US2 |
| 7 | Restore tab, `dirty-journal` image, smaller card | Warning "will be repaired and shrunk"; Shrink and write works | US3 |
| 8 | Real card from a device switched off by pulling the power: back up, repair, write back, boot | Boots; `fsck` on the device reports clean | SC-001 (hardware checklist) |

## Results

| Date | Build / tests | Scenarios | Notes |
|------|---------------|-----------|-------|
| 2026-10-17 | 0 warnings; 348 passed, 2 skipped (42 new: journal, overlay/orphans, finding kinds, repair end to end) | 1–7 (in-process UI check: File system line, Repair option, names, requests, same-size repair run → clean, damaged image refused, Restore warning) | Same-size repair of all four power-loss fixtures equals the `e2fsck -fy` reference tree (own comparer); independent check in Docker with the CI script: `e2fsck -fn` clean and 1563–1564 objects identical for `repaired-dirty-journal`, `-dirty-journal-torn`, `-orphans`, `-not-clean`. Image tab screenshots (needs repair: six languages; damaged: en/de/ja; de/ja at 800 × 560) without clipped text |
| — | — | 8 | Hardware: checklist steps 30–31 not run yet |
| — | — | SC-004 | Performance against shrinking on a 16 GiB image not measured yet (T033) |
