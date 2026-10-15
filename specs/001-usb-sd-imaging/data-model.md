# Data Model: USB/SD Card Imaging Utility

All sizes and offsets are in bytes (`long`) unless stated; sector-based values are converted
at the parsing boundary.

## DeviceInfo (Removable Medium)

| Field | Type | Notes |
|-------|------|-------|
| DiskNumber | int | `\\.\PhysicalDrive{n}` |
| DevicePath | string | interface path, used for eject |
| Model | string | vendor + product, trimmed |
| SerialNumber | string? | may be empty |
| BusType | enum `Usb, Sd, Mmc` | others never constructed |
| IsRemovableMedia | bool | false → tagged "external disk" (extra confirmation) |
| Size | long | > 0, multiple of LogicalSectorSize |
| LogicalSectorSize | int | 512 or 4096 |
| IsWriteProtected | bool | from `IOCTL_DISK_IS_WRITABLE` |
| HostsExecutable | bool | true → never a restore target |
| PartitionTable | PartitionTable | may be `Style = None/Unknown` |
| Volumes | IReadOnlyList<VolumeInfo> | mount points/letters for display & lock |

**Validation**: devices hosting system/boot/pagefile volumes are filtered out before a
`DeviceInfo` is created (never reachable by UI).

## PartitionTable

| Field | Type | Notes |
|-------|------|-------|
| Style | enum `None, Mbr, Gpt, Unknown` | Unknown = damaged/unrecognized |
| DiskGuid | Guid? | GPT only |
| Partitions | IReadOnlyList<PartitionInfo> | ordered by offset |

## PartitionInfo

| Field | Type | Notes |
|-------|------|-------|
| Number | int | 1-based as in Windows |
| Offset | long | start in bytes |
| Length | long | bytes |
| MbrType | byte? | MBR only |
| GptType / GptId | Guid? | GPT only |
| Name / Label | string | GPT name or volume label |
| FileSystem | string | e.g. FAT32, exFAT, NTFS, ext4, "unknown" |

**Validation**: `Offset + Length ≤ Device.Size`; offset and length sector-aligned.

## ImageDescriptor (Image)

| Field | Type | Notes |
|-------|------|-------|
| Path | string | `.img` or `.img.gz` |
| Format | enum `Raw, Gzip` | from extension, confirmed by magic `1F 8B` |
| Kind | enum `FullDevice, Partition` | Partition iff `.partition.json` sidecar present |
| DataSize | long? | Raw: file length; Gzip: from metadata/sidecar if known, else null |
| CompressedSize | long | file length |
| FileChecksum | ChecksumFile? | `<name>.img.gz.sha256` (gzip) or `<name>.img.sha256` (raw) |
| ContentChecksum | ChecksumFile? | `<name>.img.sha256` next to gzip; same as FileChecksum for raw |
| PartitionMetadata | PartitionMetadata? | Kind = Partition |

## PartitionMetadata (sidecar `.partition.json`)

See [contracts/file-formats.md](contracts/file-formats.md). Fields: `formatVersion`,
`tableStyle`, `partitionNumber`, `offset`, `length`, `mbrType`, `gptType`, `gptId`, `name`,
`fileSystem`, `sourceModel`, `sourceSize`, `sectorSize`, `sha256`, `createdUtc`.

## ChecksumFile

| Field | Type | Notes |
|-------|------|-------|
| Path | string | sidecar path |
| Kind | enum `File, Content` | |
| Sha256 | string | 64 lowercase hex (normalized on read) |
| Label | string | file name inside the entry (informational) |

**Validation**: exactly one entry; 64 hex chars; otherwise the sidecar is reported unreadable.

## VerificationResult

| Field | Type |
|-------|------|
| Kind | `File` / `Content` |
| Expected | string? (null = no sidecar) |
| Actual | string |
| Status | `Match`, `Mismatch`, `NotAvailable` |

## OperationRequest

| Field | Type | Notes |
|-------|------|-------|
| Kind | enum `BackupDevice, BackupPartition, RestoreDevice, RestorePartition, VerifyImage` | VerifyImage needs no Device |
| Device | DeviceInfo | source (backup) / target (restore) |
| Partition | PartitionInfo? | for partition kinds |
| Image | path + Format | |
| VerifyAfter | bool | default true for restore, false for backup |
| EjectAfter | bool | default true |
| CompressionLevel | enum `Optimal, Fastest` | gz backups only |
| ExternalDiskConfirmed | bool | required when `!IsRemovableMedia` |

**Validation (RestoreGuard)**: target not write-protected; not `HostsExecutable`;
image data size ≤ target (device or partition) size; image not stored on the target device;
for partition restore the target partition exists. Violation → `ImagingException` before any write.

## OperationProgress (snapshot, immutable)

| Field | Type |
|-------|------|
| State | OperationState |
| Phase | enum `Preparing, Transferring, Flushing, Verifying, Releasing, Ejecting` |
| Percent | double (0–100, monotonic, 100 only when State = Completed) |
| BytesProcessed / BytesTotal | long / long? |
| CurrentSpeed / AverageSpeed | double bytes/s |
| Elapsed | TimeSpan |
| Remaining | TimeSpan? (null = "calculating…") |

## OperationState (state machine)

```text
Created → Preparing → Running ──► Finalizing ──► Completed (SafeToRemove = true)
              │           │            │
              │           ├─ cancel ──►│──► Cancelled (SafeToRemove, ContentComplete = false)
              └───────────┴─ error ───►└──► Failed (ErrorCode, Offset, SafeToRemove?)
```

- `Finalizing` covers Flushing → Verifying → Releasing → Ejecting phases.
- On `Cancelled`/`Failed` the engine always attempts flush + release; `SafeToRemove` reports
  whether that succeeded (false e.g. if device vanished — then "already removed").

## OperationResult

| Field | Type |
|-------|------|
| State | Completed / Cancelled / Failed |
| SafeToRemove | bool |
| ContentComplete | bool |
| ContentSha256 / FileSha256 | string? / string? |
| Verifications | IReadOnlyList<VerificationResult> (restore / verify) |
| ErrorCode | ErrorCode? (`DeviceRemoved, ReadError, WriteError, DestinationFull, FileTooLargeForFs, CorruptImage, WriteProtected, LockFailed, SizeMismatch, VerifyMismatch, ChecksumMismatch, EjectVetoed`) |
| ErrorOffset | long? |
| EjectVetoedBy | string? (warning only; data is already safe) |
