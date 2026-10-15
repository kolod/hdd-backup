# Contract: File Formats

## Full-device image

- `*.img`: raw byte copy of the device from offset 0 to `Size`. No header. Readable/writable by
  `dd`, Win32 Disk Imager, Rufus, balenaEtcher.
- `*.img.gz`: the same bytes compressed per RFC 1952 (single member when written by this tool;
  multi-member input and trailing zero padding MUST be accepted on restore). No `FNAME` header
  field is written, because the BCL `GZipStream` does not support it.

## Partition image

- Same `.img` / `.img.gz` encoding; content = exactly the partition's byte range.
- Accompanied by `<image file name>.partition.json`. Its presence marks the image as a partition
  image; restoring a partition image without it is refused.

```json
{
  "formatVersion": 1,
  "tableStyle": "GPT",
  "partitionNumber": 2,
  "offset": 1048576,
  "length": 31914983424,
  "sectorSize": 512,
  "mbrType": null,
  "gptType": "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7",
  "gptId": "2C2B5C1E-6E1D-4F55-9C8A-1B2F3E4D5A6B",
  "name": "Basic data partition",
  "fileSystem": "exFAT",
  "sourceModel": "SanDisk Ultra",
  "sourceSize": 31914983424,
  "sha256": "<64 hex chars, uncompressed content>",
  "createdUtc": "2026-10-15T07:30:00Z"
}
```

Readers MUST reject `formatVersion` greater than supported.

## Checksum sidecars

Each sidecar is ASCII, exactly one line, `sha256sum` binary-mode format:
`<64 lowercase hex>␠*<labeled file name>\n`

| Image | Sidecar | Hash over | Label |
|-------|---------|-----------|-------|
| `disk.img` | `disk.img.sha256` | file = content | `disk.img` |
| `disk.img.gz` | `disk.img.gz.sha256` | compressed file bytes | `disk.img.gz` |
| `disk.img.gz` | `disk.img.sha256` | uncompressed content (= device/partition) | `disk.img` |

- Both hashes are computed in the same pass as the backup (content hash on the data read from
  the device, file hash on the bytes written to the file).
- Readers accept upper-case hex, text-mode marker (two spaces instead of `␠*`), and CRLF. The
  label inside is informational; the sidecar is matched by its own file name.
- Restore/verify: every sidecar present is checked; a mismatch fails the operation.

## In-progress output

- During backup the image is written as `<image>.partial` and sidecars as `<sidecar>.partial`;
  all are flushed, then renamed (sidecars first, image last). On cancel/failure all `.partial`
  files are deleted, so no checksum file remains for an incomplete image.

## Settings file

- `HddBackup.ini` next to the EXE, UTF-8, `key=value` lines, unknown keys ignored:
  `language`, `ejectAfter`, `verifyAfterRestore`, `verifyAfterBackup`, `fastCompression`,
  `lastImageFolder`.
