# Data Model: Image Conversion and Checksum Calculation

No persistent settings change; the Image tab's fast compression choice uses the existing
`FastCompression` setting.

## OperationRequest (extended)

| Field | Use |
|-------|-----|
| `Kind = ConvertImage` (new) | Convert the image at `ImagePath` to `OutputPath` |
| `ImagePath` | Source image (raw or gzip; whole device or partition) |
| `OutputPath` (new) | Destination file; format from its extension (`.gz` = gzip) |
| `Compression` | Optimal / Fastest for gzip output |
| `SaveMissingChecksums` (new, `VerifyImage` only) | After a verification without mismatch, write the missing sidecars |
| `EjectAfter` | Always false for these kinds |

## OperationResult (extended)

| Field | Use |
|-------|-----|
| `OutputPath` (new) | Committed converted image |
| `SavedChecksumFiles` (new) | File names of sidecars written by the operation (conversion or checksum calculation) |
| `FileSha256`, `ContentSha256`, `DataBytes`, `Verifications` | As for backups / verification |

## Conversion files

| Source | Output | Sidecars written for the output | Copied |
|--------|--------|-------------------------------|--------|
| `disk.img.gz` | `disk.img` | `disk.img.sha256` (content) — skipped when it exists with the same hash | `disk.img.gz.partition.json` → `disk.img.partition.json` |
| `disk.img` | `disk.img.gz` | `disk.img.gz.sha256` (file), `disk.img.sha256` (content) — skipped when it exists with the same hash | `disk.img.partition.json` → `disk.img.gz.partition.json` |

All new files are written as `.partial` and renamed after verification, the image last.

## Conversion lifecycle

```text
Preparing → Transferring (read source, hash content, write output)
          → compare source checksums ──mismatch──► Failed (partials deleted)
          → Flushing → write sidecar/metadata partials
          → Verifying (read back output, compare hashes) ──mismatch──► Failed
          → Commit (rename partials, image last) → Completed
Cancel at any point ──► Cancelled (partials deleted)
```

## Image tab state (additions)

| Control | Rule |
|---------|------|
| Size: Same as the image (default) | Enabled for any loaded image |
| Size: smallest / device / card | Unchanged (shrinkable whole-device images only) |
| Format | Default with Same as the image: opposite of the source format |
| Fast compression | Enabled when the format is compressed; value = `FastCompression` setting |
| Create image | Same size: enabled when output ≠ source and format ≠ source format; reason `Image_SameAsSource` otherwise. Shrink sizes: unchanged |
| Verify / Calculate checksums | Label "Calculate checksums" when the image has no checksum file; always sends `VerifyImage` with `SaveMissingChecksums = true` |
