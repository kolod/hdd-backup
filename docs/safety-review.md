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

## Repairing ext4 after an unclean shutdown (feature 006)

Scope: `Jbd2Journal`, `OverlayBlockDevice`, `OrphanList`, `Ext4Checker` finding kinds,
`ShrinkPlanner` / `ImageShrinker` repair mode, Image and Restore tab options.

| Rule | Implementation | Automated evidence |
|------|----------------|--------------------|
| Only images are repaired; no device is written | Repair is a mode of `ShrinkImage`, which takes an image path and never opens a device | `RepairTests.*` (source SHA-256 unchanged) |
| The source is never written | The journal is replayed into an in-memory overlay (`OverlayBlockDevice`, `CanWrite = false`, `Write` throws) over the read-only source | `OverlayAndOrphanTests.Overlay_IsReadOnly` |
| Only complete, intact transactions are applied | Commit block required; journal checksums (v2/v3) and data block checksums verified; revoke records honored; the first incomplete or damaged transaction ends the log | `Jbd2JournalTests.*` (torn, damaged data, missing commit, revoke, escape, tag formats) |
| Damage beyond an unclean shutdown is refused, not "fixed" | `Ext4Checker.CheckDetailed` on the recovered view; any `Structural` finding or recorded kernel error blocks the repair before the output is written | `RepairTests.StructuralDamage_*`, `CheckerKindTests.*` |
| The result equals what Linux recovery produces | Same verification as a shrink (own full check, tree comparison with the recovered source, prefix bytes); fixtures compared with `e2fsck -fy` references | `RepairTests.SameSizeRepair_EqualsLinuxRecovery`; CI `ext4-check` runs `e2fsck -fn` and a tree comparison against the references |

## Browsing and extracting files (feature 008)

Scope: `BrowseSource`, `Ext4FileSystemView`, `FilePreview`, `WindowsNames`, `ExtractionPlanner`,
`Ext4Extractor`, the engine's `ExtractFiles` operation, and the Files tab. Review date: 2026-11-05.
No code in `HddBackup.Core/Browsing/` has a write path to an image or a device: sources are opened
through `FileBlockDevice.OpenRead` and `IDeviceAccess.OpenForRead`, never `OpenExclusiveForWrite`,
and the journal and orphan code of feature 006 is not used.

| Rule | Implementation | Automated evidence |
|------|----------------|--------------------|
| The image and the device are never written | Read-only handles only; no lock, dismount or eject; the Files tab never asks for a write confirmation | `ReadOnlyTests.BrowsingPlanningAndExtracting_NeverWriteToTheSource` (zero `Write`/`Flush` on a `ReadOnlyBlockDevice`), `ReadOnlyTests.EngineExtraction_LeavesTheImageUnchanged_AndTouchesNoDevice` (SHA-256 before/after, no exclusive open, no eject), `DeviceBrowseTests.BrowsingADevice_NeverLocksWritesOrEjects` |
| The extraction cannot overwrite the image | `ExtractionPlanner` flags a destination file that `FileIdentity.IsSameFile` equals the image; the engine and `Ext4Extractor` refuse with `DestinationIsSource` | `ReadOnlyTests.ADestinationThatIsTheImageItself_IsRefused_AndTheImageIsUntouched`, `ExtractorTests.WritingOverTheProtectedImage_IsRefused` |
| The extraction cannot write to the browsed device | `ImagingEngine.ValidateExtract` refuses a destination whose volumes are on the device's disk (`PathInfo.DiskNumbers`) | `ReadOnlyTests.ADestinationOnTheBrowsedDevice_IsRefusedBeforeAnythingHappens` |
| Nothing is replaced without asking | The planner counts conflicts; the Files tab asks once (Replace / Skip existing / Cancel, default Cancel); the engine's default policy is Skip | `ExtractionPlannerTests.ExistingFiles_AreConflicts_SkippedOrKeptForReplacing`, `ExtractorTests.ExistingFiles_AreKeptUnderSkip_AndReplacedUnderReplace`, `ReadOnlyTests.AnExtractionThatSkipsExistingFiles_DoesNotTouchThem` |
| Free space is checked before writing | `ExtractionPlan.InsufficientSpace` against `PathInfo.FreeBytes`; the engine fails with `DestinationFull` before the first byte | `ExtractionPlannerTests.NotEnoughFreeSpace_IsReportedWithBothSizes`, `ReadOnlyTests.NotEnoughSpace_StopsBeforeWriting` |
| Partial output never looks complete | A file is written to `.hddbackup-part-<id>` and renamed when whole; the part file is deleted on cancel and on every error | `ExtractorTests.CancellingInTheMiddleOfAFile_LeavesNothingThatLooksComplete`, `CancellingBetweenFiles_KeepsTheCompletedFilesWhole`, `ASourceReadError_StopsTheExtraction_AndLeavesNoPartFile`, `DeviceBrowseTests.ADeviceThatDisappears_StopsAnExtraction_WithTheCause_AndNoPartFile` |
| Errors stop with the cause; one bad item does not stop the rest | Source device errors rethrow (`DeviceRemoved`, `ReadError`); damaged items and destination problems are listed per item | `ExtractorTests.AnUnreadableItem_IsReported_AndTheRestIsExtracted`, `ADestinationWriteProblem_FailsOnlyThatItem`, `DamagedFileSystemTests.*` |
| Names never collide or escape the destination | `WindowsNames` replaces invalid characters, trailing dots and spaces, reserved names and case-only duplicates; paths are built from mapped names only, never from raw file system paths | `WindowsNamesTests.*`, `ExtractorTests.TrickyNames_AreRenamedPredictably_AndContentSurvives` |
| Extracted data equals the source | Independent comparison with `debugfs rdump` in CI (`tools/ci/Invoke-Ext4Checks.sh`) | `ExtractorTests.WholeFileSystem_IsIdenticalToTheManifest`; local Docker run: 1560 and 1564 files identical |
| A damaged or not cleanly unmounted file system is shown as it is | No journal replay, no repair; state and warning from the superblock; unreadable entries marked | `DamagedFileSystemTests.TheJournalIsNotReplayed_SoTheContentIsTheOneOnDisk`, `PowerLossFileSystems_OpenWithTheirState_AndTheImageStaysUntouched` |

Not automated: the modal dialogs of the Files tab (folder picker, conflict question, message
boxes), a real removable device and Windows 7 SP1 are covered by the manual hardware checklist
(steps 32–42).

## Diagnostic tools (feature 009)

Scope: `HddBackup.Core/Diagnostics/` (`SystemReader`, `DiagnosticsRunner`, `LinuxRoot`, `SshdConfig`,
`AccountDatabase`, `SshCheck`, `SystemdScan`, `CronScan`, `AutostartScan`, `StartupInventory`,
`ReportBuilder`), the Tools button, `SshWizardDialog`, `StartupTasksWindow` and `ReportActions` in the
App. Review date: 2026-11-05. The tools only read the opened image or device; the constitution
amendment 1.4.0 extends Principle VII to read-only diagnostics.

| Rule | Implementation | Automated evidence |
|------|----------------|--------------------|
| The image and the device are never written | `DiagnosticsRunner` opens its own read-only source through `BrowseSource` (`FileBlockDevice.OpenRead`, `IDeviceAccess.OpenForRead`), never `OpenExclusiveForWrite`; `SystemReader` has no write path | `DiagnosticsRunnerTests.RunsAToolOnADeviceThroughTheReadHandleOnly` (zero exclusive opens, zero writes), `TheImageIsNotModified` (SHA-256 before/after), `SshFixtureTests.TheImageIsNotModified`, `DiagnosticsPerformanceTests` (zero `Write` calls) |
| The source is released after every run | `DiagnosticsRunner` disposes the file system and the source in `finally` | `DiagnosticsRunnerTests.ReleasesTheSourceWhenTheToolFailsOrIsCancelled`, `ADeviceThatFailsMidwayThrowsAnImagingExceptionAndIsReleased`, `DegradedSourceTests.ADeviceThatFailsWhileAToolRunsAbortsWithTheCauseAndReleasesTheSource` |
| No secret is ever read into a result | Password fields become a `PasswordState` and key lines are only counted inside the parsers (`AccountDatabase`); no result type has a field that can hold a hash, a key or a private key; private key files are never opened | `AccountDatabaseTests.NoAccountObjectHoldsAPlantedSecret`, `SecretLeakTests.*` (every field of every result of five real images, all matrix cases and the reference system, planted `$6$PLANTEDSECRET…`, `PLANTEDKEYBLOB`, a private key file), `SshFixtureTests.NeverOpensThePrivateKey` |
| No secret reaches the clipboard or a saved file | `ReportBuilder` reads only the structured results | `ReportBuilderTests.NoReportOfAFixtureContainsASecret` |
| Symbolic links are resolved inside the image, never on the host | `SystemReader` walks the components through `IFileTree` with a limit of 40 links | `SystemReaderTests.*` (absolute, relative, chained, merged `/usr`, loops, `/dev/null`) |
| The reported facts equal what the real programs say | `SshdConfig` follows `sshd -T` (first value wins, `Include`, `Match` conditional); `SystemdScan` follows `systemctl is-enabled` | `check-diagnostics.sh` in the `ext4-check` job compares the exported settings with `sshd -T` and the enabled units with `systemctl --root` for every fixture (local Docker run: all identical) |
| An unreadable or damaged source does not crash a tool | Read problems become return values and notes; only errors of the source (`DeviceRemoved`, `ReadError`) abort, with the cause, and no window opens | `SystemReaderTests.AnUnreadableEntryIsAValueNotAnException`, `DegradedSourceTests.*`, `StartupInventoryTests.OneUnreadableSourceMarksItsEntriesAndDoesNotStopTheScan` |
| "SSH is off" is never said when something unread could matter | The verdict `NotEnabledBootFlagPossible` for Raspberry Pi systems with an unread FAT boot partition | `SshCheckMatrixTests` (case 17), `SshFixtureTests.RaspberryPiLikeImageNeverSaysSshIsOff` |
| Advice never becomes an action | Neither window has a control that changes the image; the Tools button is a menu of two read-only tools; there is no network access | Code review; `Tools` strings state "Nothing is changed" |
| Saving never replaces a file silently | `SaveFileDialog.OverwritePrompt = true`; text is written as UTF-8 with a byte order mark | Manual checklist step 45 (the dialog is not automated) |

Not automated: the modal windows and the Save as… dialog, a real removable device, timing on a
16–32 GiB image over USB 2.0 and Windows 7 SP1 are covered by the manual hardware checklist
(steps 43–45). The independent `sshd -T` and `systemctl --root` comparison runs in the `ext4-check`
CI job and was run locally in Docker (debian:bookworm).

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
