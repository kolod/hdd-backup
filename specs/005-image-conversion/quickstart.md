# Quickstart: Validating Image Conversion and Checksum Calculation

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all tests pass, including the new conversion tests (gzip → raw and raw →
gzip round trips for MBR, GPT and partition fixtures; source checksum mismatch; cancellation and
failure leave no files; sidecar rules from data-model.md) and checksum tests (calculate for raw
and gzip; complete a missing content sidecar; nothing saved on mismatch).

## Independent check of results

In Git Bash or WSL, next to the converted files:

```bash
sha256sum -c disk.img.sha256          # uncompressed output or content of the gzip output
sha256sum -c disk.img.gz.sha256       # gzip output file
gzip -dc disk.img.gz | cmp - disk.img # gzip output decompresses to the original
```

## Manual scenarios

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Image tab, `rpi-like-4k.img.gz` | Size "Same as the image" selected, format Uncompressed, output `rpi-like-4k.img` | FR-001, FR-007 |
| 2 | Create image | Progress "Converting the image…", then verifying; result names `rpi-like-4k.img` and its checksum file; source unchanged | US1 |
| 3 | Select the new `rpi-like-4k.img`, format Compressed, fast compression on, output another folder | `.img.gz` with two checksum files; `gzip -dc` equals the `.img` | US2 |
| 4 | Same size, same format as the source | Create disabled with the reason | FR-008 |
| 5 | Partition image (`*.partition.json`) → convert | Output has its `.partition.json`; Restore tab accepts it for a partition | FR-005 |
| 6 | Image without checksum files | Button "Calculate checksums"; after it, files exist, button reads "Verify image", Restore tab lists them | US3 |
| 7 | `.img.gz` with only `.img.gz.sha256` | Verify image adds `.img.sha256` | FR-011 |
| 8 | Corrupt one byte of a `.img.gz` with checksums, then convert | Fails with checksum mismatch; no output files | FR-003, SC-005 |
| 9 | Cancel a conversion and a checksum calculation | No output, partial or checksum files left | SC-005 |
| 10 | Uncompressed output larger than the free space | Refused before writing | Edge case |

## Results

| Date | Build / tests | Scenarios | Notes |
|------|---------------|-----------|-------|
| | | | |
