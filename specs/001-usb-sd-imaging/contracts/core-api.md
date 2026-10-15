# Contract: Core API (UI ↔ HddBackup.Core)

The UI depends only on these types. Signatures are normative; bodies are implementation.
Types referenced but not defined here are in [../data-model.md](../data-model.md).

## Block device abstraction

```csharp
public interface IBlockDevice : IDisposable
{
    long Size { get; }                 // bytes, multiple of SectorSize
    int SectorSize { get; }
    bool CanWrite { get; }
    // offset and count MUST be multiples of SectorSize; buffer may be any byte[] in fakes,
    // implementations requiring alignment copy via AlignedBuffer.
    int Read(long offset, byte[] buffer, int index, int count);
    void Write(long offset, byte[] buffer, int index, int count);
    void Flush();                      // commits OS + device caches
}
```

Implementations: `PhysicalDiskDevice` (Win32), `PartitionView` (offset/length window over
another device), test fakes `MemoryBlockDevice`, `FaultyBlockDevice`.

## Device discovery

```csharp
public interface IDeviceEnumerator
{
    // Returns only USB/SD/MMC disks not hosting system, boot or pagefile volumes.
    IReadOnlyList<DeviceInfo> GetDevices();
}

public interface IDeviceAccess
{
    IBlockDevice OpenForRead(DeviceInfo device);
    // Locks + dismounts all volumes of the device; throws ImagingException(LockFailed) without
    // writing anything if any volume cannot be locked. Disposing releases locks and triggers
    // a partition-table rescan.
    IExclusiveDevice OpenExclusiveForWrite(DeviceInfo device);
    EjectResult Eject(DeviceInfo device);   // Ok | Vetoed(by) | NotSupported
    PathInfo GetPathInfo(string path);      // disks hosting the path, free space, file system
}
// The Windows implementation re-checks the device identity (system-disk guard, bus, model,
// serial) before opening a disk, so a stale selection cannot reach another disk.
```

## Operations

```csharp
public interface IImagingEngine
{
    // Validation runs synchronously; throws ImagingException before any I/O on violation and
    // returns warnings (TargetLarger, ImageSizeUnknown, NoStoredChecksum, ...) to acknowledge.
    IReadOnlyList<ValidationWarning> Validate(OperationRequest request);

    // Runs on background threads; the returned task completes with the final result and
    // never faults for expected errors (they are reported via OperationResult.ErrorCode).
    Task<OperationResult> RunAsync(OperationRequest request, CancellationToken cancel);

    // Thread-safe, lock-free; UI polls every 250 ms.
    OperationProgress CurrentProgress { get; }
}
```

### Behavioral guarantees

1. `Percent` is monotonic non-decreasing and `< 100` until `State == Completed`.
2. `Completed` is entered only after: last write returned, `Flush()` succeeded, verification
   (if requested) passed, all device handles closed, and eject finished (if requested; a veto
   still yields `Completed` with `EjectVetoedBy` set since data is already committed).
3. Backup writes image and checksum sidecars as `.partial`, flushes all
   (`FileStream.Flush(true)`), then renames (sidecars first, image last); on cancel/failure all
   `.partial` files are deleted. SHA-256 (content and, for gzip, file) is computed in the same
   pass — never by re-reading the device or the file.
7. Restore computes image checksums in the same pass as writing; if any present sidecar
   mismatches, the result is `Failed(ChecksumMismatch)` (device still flushed and released).
   `VerifyImage` reads only the image file and never opens a device.
4. On cancel or error after any write to a device, the engine flushes and releases the device
   before reporting, and sets `SafeToRemove` accordingly.
5. Errors carry the absolute byte offset at which the failing I/O started.
6. The source device is never opened with write access during backup.

## Errors

```csharp
public sealed class ImagingException : Exception
{
    public ErrorCode Code { get; }
    public long? Offset { get; }
    public string Detail { get; }   // English technical detail for logs; UI shows localized
                                    // text by Code with Detail/Offset as parameters.
}
```
