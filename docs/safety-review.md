# Safety review: device write paths (constitution Principle I)

Review date: 2026-10-15. Scope: every code path that can write to a physical device:
`ImagingEngine.RunRestore`, `DeviceLock`, `PhysicalDiskDevice.Write`, and the UI that starts
them (`MainForm.StartOperation`, `ConfirmWriteDialog`).

| Rule | Implementation | Automated evidence |
|------|----------------|--------------------|
| Only USB/SD/MMC media are listed | `DeviceEnumerator` filters by `STORAGE_DEVICE_DESCRIPTOR.BusType` ∈ {7, 0x0C, 0x0D} | Manual checklist step 2; on the dev machine (NVMe and SATA disks only), the enumerator listed 0 devices |
| System, boot and page-file disks are never listed | `SystemDiskGuard` (Windows directory, `%SystemDrive%`, `ExistingPageFiles`, `HKLM\SYSTEM\Setup\SystemPartition`) | On the dev machine, boot/system NVMe disk #4 was detected as protected |
| The disk the program runs from is never a target | `DeviceInfo.HostsExecutable`, excluded from the Restore list and refused by `RestoreGuard` | `RestoreGuardTests.TargetHostingTheExecutable_IsRefused` |
| A stale device selection cannot hit another disk | `WindowsDeviceAccess.VerifyIdentity` re-checks the system guard, bus, model and serial; the engine re-checks the size | `RestoreGuardTests.DeviceSizeChangedSinceListing_IsRefusedBeforeWriting` (size); identity is covered by the manual checklist |
| Explicit confirmation before writing | `ConfirmWriteDialog`: Cancel is the default and Accept button; Write is enabled after 2 s; external disks need a checkbox | `RestoreGuardTests.ExternalDisk_RequiresConfirmation` (engine side) |
| Exclusive access or abort | `DeviceLock` locks and dismounts every volume and throws `LockFailed` before the disk is opened for writing | `LockFailureTests.LockFailure_WritesNothing` |
| Oversized image refused; larger target warned | `RestoreGuard` (known size), runtime `SizeMismatch` for gzip with unknown size | `RestoreGuardTests.OversizeImage_IsRefused`, `GzipImageLargerThanTarget_FailsWithOffset_AndReleasesDevice`, `LargerTarget_ProducesWarning` |
| Image stored on the target is refused | `RestoreGuard` via volume disk extents of the image path | `RestoreGuardTests.ImageStoredOnTarget_IsRefused` |
| Write-protected target refused | `DeviceInfo.IsWriteProtected` + `IOCTL_DISK_IS_WRITABLE` in `DeviceLock` | `RestoreGuardTests.WriteProtectedTarget_IsRefused` |
| Source opened read-only | `PhysicalDiskDevice.Open(writable: false)` (GENERIC_READ only) | `BackupSafetyTests.Source_IsNeverWritten` |
| Errors stop the operation with an offset | All device errors map to `ImagingException` with an absolute offset | `BackupSafetyTests.ReadError_*`, `DeviceRemovedTests.*`, `RestoreVerifyTests.ReadBackMismatch_IsReportedWithExactOffset` |
| Partial output never looks valid | `.partial` files are renamed only after flush (sidecars first, image last) and deleted on failure | `BackupSafetyTests.ReadError_ReportsOffset_AndLeavesNoFiles`, `ExistingImageAndSidecars_SurviveAFailedBackup`, `CancellationTests.CancelledBackup_LeavesNoFiles` |
| Cancellation leaves a defined state | The engine always flushes and then releases after any write; the result reports `SafeToRemove` and `ContentComplete` | `CancellationTests.CancelledRestore_FlushesThenReleases` (random points) |
| 100% only when safe to remove | `PhaseModel` caps the bar at 99.9% until `Completed`; `Completed` comes after flush, verify, release and eject | `BackupProgressTests.PercentStaysBelow100UntilCompleted_AndReaches100`, `ProgressModelTests.*` |

## Residual risks

1. **Automatic remount after a partition table change.** When `DeviceLock` is disposed, Windows
   re-reads the new table and may mount new volumes. This happens only after all data has been
   written and flushed, so it does not affect data integrity.
2. **Device cache honesty.** `FlushFileBuffers` sends SYNCHRONIZE CACHE. A device that
   acknowledges writes it has not stored can still lose data on removal. Read-back verification
   (on by default for restores) reduces this risk but cannot detect data that is lost later.
3. **Eject vetoed by another process.** The data is already committed and the program has
   closed its handles. The UI shows a warning with the name of the vetoing process instead of a
   plain "safe to remove".
4. **Native code coverage.** Enumeration, locking, raw I/O and eject are verified only by the
   manual hardware checklist. They need a run on real USB and SD media on Windows 7 SP1 and
   Windows 11 before release.
