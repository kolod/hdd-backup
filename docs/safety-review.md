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

## Shrinking ext4 images (feature 002)

Scope: `ShrinkPlanner`, `ImageShrinker`, `Ext4Writer`, `PartitionTableEditor`, the engine's
`ShrinkImage` operation, and the "shrink and write" flow in `MainForm`.

| Rule | Implementation | Automated evidence |
|------|----------------|--------------------|
| The original image is never modified | Source opened read-only (`FileBlockDevice.OpenRead`); a gzip source is decompressed to a separate working copy | `ImageShrinkerTests.Shrink_ProducesVerifiedSmallerImage` and `Cancellation_*` compare the source SHA-256 before and after |
| Unsafe sources are refused before any file is created | `ShrinkPlanner.Analyze` blockers (layout, features, journal recovery, not clean, errors, orphans, block/inode size, checksum) checked by `ImagingEngine.ValidateShrink` | `ShrinkRefusalTests.*` (negative fixtures and patched superblocks) |
| A result is kept only if it is verified | Three steps on the finished output: own `Ext4Checker`, `Ext4TreeComparer` against the source (content, metadata, xattrs, links), and byte comparison of everything before the root partition except the table entry | `ImageShrinkerTests.*`; independent `e2fsck -fn` and debugfs comparison logged in `specs/002-ext4-image-shrink/quickstart.md` |
| Partial output never looks valid | Output and working copy are `.partial` / `.work.partial`, renamed only after verification and sidecars; deleted on failure or cancel | `TargetBelowMinimum_IsRefusedWithoutFiles`, `Cancellation_LeavesNoFiles_AndSourceUntouched` |
| "Shrink and write" writes only a verified, committed image | The write step is a normal `RestoreDevice` of the committed output, with the usual confirmation dialog, lock, guards and read-back verification | `ShrinkAndWriteTests.ShrunkImage_WrittenToSmallerDevice_EqualsShrunkImage` |
| 100% only at the end | Shrink phases use `PhaseModel` (capped at 99.9% until `Completed`) | `ImageShrinkerTests.Progress_StaysBelow100UntilCommit` |

The product never calls Linux tools; Docker is used only on the developer machine to create
fixtures and to run the independent checks.

## Release integrity (feature 003)

Scope: `.github/workflows/release.yml`, `.github/actions/build-test`, `.github/rulesets/`,
`tools/release/`, `tools/github/Set-RepoProtection.ps1`.

| Rule | Implementation | Evidence |
|------|----------------|----------|
| Releases are built only from reviewed code on main | `Test-ReleaseTag.ps1` checks `git merge-base --is-ancestor <tag> origin/main`; main accepts only squash-merged PRs with green `build-test` and `ext4-check` (`main.json`) | `Test-ReleaseTag.Tests.ps1` (side-branch tag → exit 2); `Rulesets.Tests.ps1` |
| A release build is the same as a CI build | Both use the composite action `build-test` (warnings as errors, all tests, Pester, artifact check) | Workflow files |
| Nothing becomes public without the hardware checklist | `gh release create --draft`; the body starts with a "hardware checklist pending" banner | `New-ReleaseBody.Tests.ps1` (banner text and order) |
| A published release never changes | The workflow refuses an existing release; immutable releases are enabled; `v*` tags can be created only by the owner and cannot be moved or deleted by others | `release.yml` validate job; `Set-RepoProtection.Tests.ps1`; `Rulesets.Tests.ps1` |
| Downloads can be verified | `SHA256SUMS` asset plus the same lines in the release text | `New-ReleaseBody.Tests.ps1` (hashes equal `Get-FileHash`) |
| The build cannot be changed through a third party | Only GitHub-owned actions, pinned to full commit SHAs; Dependabot proposes updates as PRs that go through CI | Workflow files, `.github/dependabot.yml` |
| Least privilege | Workflows default to `contents: read`; only the release `publish` job has `contents: write`; pull requests from forks get no secrets | Workflow files |
| No stored admin credential | Protection is applied by the owner locally with `gh`; nothing in CI can change the rules | `Set-RepoProtection.ps1` (admin check, exit 3) |

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
5. **ext4 rebuild fidelity.** The rebuilt file system is checked by the program's own checker
   and tree comparison, and during development by e2fsprogs 1.47.0 on all fixtures. File
   systems with features outside the supported set are refused rather than rebuilt. A boot test
   on a real Raspberry Pi (checklist steps 19–24) is still required before release.
6. **Unsigned executable.** Releases are not code-signed; users rely on the SHA-256 published
   with each release. Windows SmartScreen may warn on first start.
7. **Owner account compromise.** The owner can bypass the main-branch rules (through a pull
   request) and create release tags. Two-factor authentication on the owner account is assumed.
