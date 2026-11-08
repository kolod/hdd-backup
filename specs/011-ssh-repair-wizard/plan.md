# Implementation Plan: Repairing SSH Access from the SSH Check Wizard

**Branch**: `011-ssh-repair-wizard` | **Date**: 2026-11-08 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/011-ssh-repair-wizard/spec.md`

## Summary

The SSH check wizard (feature 009) gets a repair layer on top of the edit mode of feature 010.
A new Core planner turns a reason of the SSH check into a **Repair**: a named group of
`EditChange`s computed from the file system as it will be (the `PendingTree`, so repairs stack
on each other and on hand edits). The wizard shows **Fix…** / **Add a key…**, a confirmation window
with the exact change, the list of waiting repairs with **Undo**, and re-runs the check against the
pending tree after every repair. Images queue the repair in the diff file (new `Group` marker so a
repair can be undone as a unit); cards write the whole repair at once with one preflight.
To express repairs the 010 writer learns two things it lacks: **creating a symbolic link** and
**setting mode / owner / group** (also for newly created entries). The SSH check itself gets one new
fact it needs for a correct verdict: whether the server will ignore an account's keys because of
unsafe modes (`StrictModes`).

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: BCL and Windows Forms only; feature 009 (`SshCheck`, `SshdConfig`,
`AccountDatabase`, `SystemReader`), feature 010 (`EditChange`, `PendingTree`, `ImageEditSession`,
`CardEditSession`, `Ext4Editor`); `System.Security.Cryptography.SHA256` for key fingerprints; no new
packages

**Storage**: nothing new. Repairs live in the existing diff file (format version 2 adds a group id
and a repair title code per change; version 1 files stay readable); a card keeps nothing

**Testing**: xUnit; planner and key parser against in-memory trees and real ext4 fixtures
(`edit-ssh*` made by `make-editing-fixtures.sh`), end-to-end through `ImageEditSession` with the
existing fault-injecting device, the 010 `e2fsck -fn` oracle over every saved image the new tests
export; a "no secret in output" test extended from feature 009's `SecretLeakTests`

**Target Platform**: Windows 7 SP1 – Windows 11, x86/x64

**Project Type**: desktop app (single WinForms executable; Core compiled in)

**Performance Goals**: planning a repair ≤ 1 s on a real image (reads a handful of small files);
re-check after a repair ≤ 2 s (as feature 009); card: one preflight per repair (≈ 12 s on a 15 GiB
card, as measured in 010 R-7), not one per change

**Constraints**: nothing is written by opening the wizard or paging (FR-003); all writes go through
010 (partition-limited, ordered, preflight-checked); no secret (hash, private key, existing key
data) in logs, diff names, reports or screens; executable stays under 5 MB

**Scale/Scope**: ~6 new Core classes (~1 500 lines) + ~250 lines of writer additions, 2 small
dialogs, ~70 strings × 6 languages, ~4 new fixtures

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ amendment 1.5.0 already covers editing ext4 on removable media / images with confirmation, range limit, ordered writes, pending file | ✅ no new write path: repairs are `EditChange`s run by the same sessions; new writer operations (symlink, attributes) obey the same write order (R-3); only the edited partition is written (device wrapper); a card repair is confirmed before the first byte (010 dialog) and checked as a whole before the first write (R-9) |
| II | Faithful Imaging | ✅ untouched | ✅ images change only on **Save**, as in 010 |
| III | Portable & Compact | ✅ no dependencies | ✅ no new files beside the image; no registry; strings only |
| IV | Win 7 – 11 | ✅ managed code | ✅ SHA-256 from the BCL; file picker for `.pub` is the standard `OpenFileDialog` |
| V | Localized UI | ✅ new strings in six languages | ✅ Core gives codes and arguments, texts come from resources; parity test extended |
| VI | Test-Verified Disk I/O | ✅ logic behind `IBlockDevice`/`IFileTree` | ✅ fixtures + `e2fsck -fn` oracle; interruption injected at every write of a repair; zero-write proof for opening the wizard |
| VII | Simplicity | ✅ small, text-level repairs | ✅ no general sshd_config editor, no `Match` handling, no passwords, no host keys, no legacy init (R-5, R-7) |

No violation. The constitution needs no amendment; Complexity Tracking lists the two writer
additions.

## Project Structure

### Documentation (this feature)

```text
specs/011-ssh-repair-wizard/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/ui.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
src/HddBackup.Core/Editing/                         # writer additions (feature 010 code)
├── EditChange.cs           # + kinds CreateSymlink, SetAttributes; Mode/Uid/Gid on creations; Group + Title
├── Ext4EditorStructure.cs  # + CreateSymlink (fast, ≤ 59 bytes), SetAttributes; creations take mode/owner
├── PendingTree.cs          # + symlink nodes, attribute overrides
├── DiffFile.cs             # format v2 (group, title, attributes); v1 read unchanged
├── ImageEditSession.cs     # + AddRepair(IEnumerable<EditChange>), Undo(group), Groups
└── CardEditSession.cs      # + Apply(IReadOnlyList<EditChange>): one editor, one preflight, one write set

src/HddBackup.Core/Diagnostics/
├── SshCheck.cs             # + key-file safety finding (StrictModes); verdict counts unsafe keys as unusable
├── SshdConfig.cs           # + exposes StrictModes, per-setting line spans needed by the planner
└── AccountDatabase.cs      # + KeyFilePath per account (the file the server reads), key-file modes

src/HddBackup.Core/Repair/                          # NEW
├── PublicKey.cs            # parse/validate one public key line; type, fingerprint, comment, options; private-key detection
├── SshRepair.cs            # Repair, RepairKind, RepairPlan (title code, args, changes, risk, explanation)
├── SshRepairPlanner.cs     # reason → repair(s); Add-key plan; works on SystemReader over a PendingTree
├── SshdEdit.cs             # line-preserving edit of sshd_config text (comment out, replace token, insert before Match)
├── PasswdEdit.cs           # change one field of one /etc/passwd line
└── RepairLog.cs            # repairs done in this session, for the saved report (no key data)

src/HddBackup.App/Forms/
├── SshWizardDialog.cs      # Result page: Fix…/Add a key… buttons, waiting repairs, Undo, "after repairs" verdict
├── RepairConfirmDialog.cs  # NEW: exact change, risk text, Apply/Cancel
├── AddKeyDialog.cs         # NEW: key box, Load…, parsed type/fingerprint/comment, target file, Add/Cancel
├── RepairTexts.cs          # NEW: repair/risk/refusal codes → localized text
└── FilesTab.Edit.cs        # SshRepairHost: exposes edit state, pending tree, Apply/Undo to the wizard
src/HddBackup.App/Resources/Strings*.resx           # ~70 strings × 6

tests/HddBackup.Core.Tests/Repair/                  # PublicKeyTests, SshdEditTests, PasswdEditTests,
                                                    # SshRepairPlannerTests, SshRepairEndToEndTests (fixtures, e2fsck export),
                                                    # RepairInterruptionTests, RepairSecretLeakTests
tests/HddBackup.Core.Tests/Editing/                 # SymlinkAndAttributeTests, DiffFileV2Tests
tests/fixtures/ext4/make-editing-fixtures.sh        # + edit-ssh-* fixtures
tests/fixtures/ext4/check-editing.sh                # + modes/owners/links comparison
docs/manual-hardware-checklist.md, README.md, docs/release-notes.md, docs/safety-review.md
```

**Structure Decision**: the planner is a pure Core function `(SystemReader over IFileTree, report,
choice) → RepairPlan`; it never writes. The App hands the plan's changes to the same
`ImageEditSession`/`CardEditSession` that the Files tab already owns, through one small host
interface, so the wizard (non-modal since feature 009) needs no knowledge of sessions. The wizard
re-runs `SshCheck` on a `PendingTree` view provided by the host after each change.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Writer gains `CreateSymlink` and `SetAttributes` (mode / uid / gid) | Enabling a service is a symlink; a `.ssh` folder the server accepts needs mode 700/600 and the account's owner; neither is possible with 010's create/edit/delete/rename | Writing the key file with 010's default mode and parent's owner makes sshd ignore the key (`StrictModes`), so the repair would "succeed" and still fail to log in |
| Diff file format v2 | Undo of a repair as a unit needs a group id per change; attributes need fields | Keeping groups only in memory loses them when the program is closed with waiting repairs, and the user could then undo half of a repair |
| SSH check learns key-file safety | A key in a file with unsafe modes is ignored; without the finding the wizard would claim "SSH should work" after a repair that cannot work (and cannot offer repair (d) at all) | Doing the check only inside the planner would make the verdict and the repair disagree |
