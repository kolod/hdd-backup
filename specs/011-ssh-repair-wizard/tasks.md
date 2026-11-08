---

description: "Task list for feature 011: repairing SSH access from the SSH check wizard (fixes and public keys)"
---

# Tasks: Repairing SSH Access from the SSH Check Wizard

**Input**: Design documents from `/specs/011-ssh-repair-wizard/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md;
features 009 (SSH check) and 010 (editing) are merged; constitution 1.5.0 needs no change.

**Tests**: Included — constitution Principle VI (test-verified disk I/O) and Principle I (every
safety rule has a test); SC-001 to SC-007 are defined against prepared images, the product's own
checker and an independent `e2fsck -fn`.

**Organization**: Foundational (writer additions, check additions, host seam) → US4 read-only gate
(P1) → US1 add a key (P1, MVP) → US2 fixes (P1) → US3 waiting repairs / undo (P2) → Polish.
US4 comes first in the build order because it is the safety condition for every repair control.

## Format: `[ID] [P?] [Story] Description`

- Paths: `src/HddBackup.Core/Editing/`, `src/HddBackup.Core/Diagnostics/`, `src/HddBackup.Core/Repair/`,
  `src/HddBackup.App/Forms/`, `tests/HddBackup.Core.Tests/Repair/`, `tests/HddBackup.Core.Tests/Editing/`,
  `tests/HddBackup.Core.Tests/Diagnostics/`, `tests/fixtures/ext4/`, `tools/ci/`
- Strings: every new key goes into all six `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx`
  (English texts in `contracts/ui.md`); the session helper `add_strings.py`/`Add-Strings.ps1` pattern from
  feature 010 may be reused; `ResourceParityTests` must pass. **Never pipe a helper's output into
  `Select-Object -First` or `head`.**
- Core returns codes and arguments; texts are built in the App (`RepairTexts`).
- **Safety rules every task must keep**: opening the wizard or paging writes nothing; every write is an
  `EditChange` run by `ImageEditSession`/`CardEditSession` (partition-limited, ordered, preflighted); no
  password hash, private key or existing key data in any output; the 009 and 010 tests keep passing.
- Python and `sed` mangle `\n` in C# string literals; edit such tests with the Edit tool.

---

## Phase 1: Setup — gate and fixtures

- [X] T001 Get the proposed layout mockup in `specs/011-ssh-repair-wizard/spec.md` approved by the maintainer; change its heading to `(approved)` and the **Status** line to `Draft (layout approved <date>)`. **Gate: no App task (T017–T019, T027–T029, T038–T042, T044, T047) starts before this.**
- [X] T002 [P] Extend `tests/fixtures/ext4/make-editing-fixtures.sh` with the `edit-ssh-*` images of research R-11 (`ok`, `disabled`, `masked`, `nokey`, `allowusers`, `badshell`, `badmodes`, `dropin`, `keyscommand`): a small Debian-like tree (`/etc/ssh/sshd_config` with and without `Include`/`Match`, `/etc/passwd`, `/etc/shadow`, `/etc/shells`, `/etc/group`, `/lib/systemd/system/ssh.service` with `[Install]`, `/etc/systemd/system/*.wants/`, home of `pi` with real owner 1000:1000) made with `mke2fs -d` as root; write their `.manifest.tsv` including mode, uid, gid and link targets
- [X] T003 [P] Extend `tests/fixtures/ext4/check-editing.sh` to compare mode/uid/gid/link target of expected entries via `debugfs -R "stat"` and to run `e2fsck -fn` on each exported saved image; call it from `tools/ci/Invoke-Ext4Checks.sh` for the repair exports
- [X] T004 Run the Docker script, commit the generated `tests/HddBackup.Core.Tests/Fixtures/ext4/edit-ssh-*.img.gz` and manifests; add them to the test project as embedded/copied fixtures exactly like `edit-basic`; add `EditFixtures` helpers (`SshImage(name)`) in `tests/HddBackup.Core.Tests/Editing/EditFixtures.cs`

---

## Phase 2: Foundational — writer, check and seam (blocks all stories)

### Writer: symlinks, attributes, groups (research R-1, R-2, R-3, R-9)

- [X] T005 [P] Write failing tests `tests/HddBackup.Core.Tests/Editing/SymlinkAndAttributeTests.cs`: create a fast symlink (path, target ≤ 59 bytes, mode 0120777, checker-clean, `e2fsck`-clean export); target ≥ 60 bytes refused (`EditError.TooLong`); `SetAttributes` changes only the given of mode/uid/gid, keeps type bits and mtime, updates inode checksum and ctime; missing path → `NotFound`; creations with mode/uid/gid land in one inode write; interruption at every write leaves checker-clean file system (leaked inode only); deleting a created symlink frees it
- [X] T006 [P] Write failing `tests/HddBackup.Core.Tests/Editing/DiffFileV2Tests.cs`: round trip of the new kinds, group, title, attributes; a v1 file is read as before with `Group = 0`; a file with an unknown future kind is "unreadable"; atomic replace and seal still hold
- [X] T007 Add `EditKind.CreateSymlink = 6`, `SetAttributes = 7`, `RepairKind`/`RepairTitle`, `LinkTarget`, `Mode`, `Uid`, `Gid`, `Group`, `Title` and factory methods (`EditChange.NewSymlink`, `EditChange.Attributes`, mode/owner parameters on `NewFile`/`NewDirectory`) to `src/HddBackup.Core/Editing/EditChange.cs`; extend `EditChange.Apply` and `WithPath`
- [X] T008 Implement `CreateSymlink`, `SetAttributes` and mode/owner on creations in `src/HddBackup.Core/Editing/Ext4EditorStructure.cs` (fast symlink in `i_block`, extents flag off, inode checksum, directory entry type 7; phases per R-3); new `EditError.TooLong` if missing in `src/HddBackup.Core/Editing/Ext4Editor.cs`; make T005 pass
- [X] T009 Teach `src/HddBackup.Core/Editing/PendingTree.cs` the new kinds: symlink nodes (`LinkTarget`, `Kind = Symlink`), attribute overrides in `Make`/`Fill` (`Mode`, `Uid`, `Gid`), `OriginalStampFor` unchanged; add cases to `tests/HddBackup.Core.Tests/Editing/PendingTreeTests.cs`
- [X] T010 Implement `DiffFile` format v2 in `src/HddBackup.Core/Editing/DiffFile.cs` (presence flags per field, version 2 header, v1 reader kept, newer-version refusal text); make T006 pass
- [X] T011 Add to `src/HddBackup.Core/Editing/ImageEditSession.cs`: `AddRepair(IReadOnlyList<EditChange>, RepairTitle)` (dry-run on a `PendingTree` and a scratch editor first; add all or none; returns the group id), `Undo(int group)` → `UndoResult` (refused when a later change touches the same path), `Groups`; tests in `tests/HddBackup.Core.Tests/Editing/ImageEditSessionTests.cs` (add, undo one of two, undo blocked by later hand edit, groups survive closing and reopening, Save applies all groups)
- [X] T012 Add `Apply(IReadOnlyList<EditChange>, progress, ct)` to `src/HddBackup.Core/Editing/CardEditSession.cs` (one `Ext4Editor`, one `EditPreflight.Check`, one write set; the single-change overload delegates); tests in `tests/HddBackup.Core.Tests/Editing/CardEditSessionTests.cs` (three changes → one preflight, zero writes outside the partition, one failing change writes nothing, interruption at every write keeps a checker-clean file system)

### Check: key-file safety (research R-6)

- [X] T013 [P] Write failing tests in `tests/HddBackup.Core.Tests/Diagnostics/SshCheckMatrixTests.cs` and `AccountDatabaseTests.cs`: key file / `.ssh` / home group- or world-writable or owned by someone else → finding `AccountKeyFileUnsafe` and the key does not count (StrictModes on); same with `StrictModes no` → note only, key counts; `AuthorizedKeysFile` patterns with `%h %u %%` give `KeyFilePath`; no key file → `KeyFilePath` is the first pattern
- [X] T014 Implement in `src/HddBackup.Core/Diagnostics/SshdConfig.cs` (`StrictModes` with source/line), `AccountDatabase.cs` (`KeyFilePath`, `KeyFileSafety`), `DiagnosticsTypes.cs`/`SshReport.cs` (`FindingCode.AccountKeyFileUnsafe`, `LoginReason.KeyFileUnsafe` in `ReasonOrder`), `SshCheck.cs` (finding, verdict); update the 009 fixtures/tests that now correctly report unsafe modes and list each updated test in the commit message; the 009 suite passes

### Seam to the App

- [X] T015 Define `ISshRepairHost` in `src/HddBackup.App/Forms/ISshRepairHost.cs` (`CanRepair`, `WhyNot`, `IsCard`, `Changed` event, `CurrentTree`, `Apply(RepairPlan)` → `Task<RepairResult>`, `Undo(int group)`, `Waiting`) exactly as research R-10; no behaviour yet
- [X] T016 [P] Add `tests/HddBackup.Core.Tests/Repair/RepairTypesTests.cs` for `RepairPlan`/`RepairTitle` invariants (immutable, `Changes` ordered, risk plans only in `Alternatives`, `Title` arguments never contain key text) and create `src/HddBackup.Core/Repair/SshRepair.cs` with the types of data-model.md so later tasks compile

**Checkpoint**: the writer can create links and set attributes, the diff file remembers repair groups, the SSH check knows about unsafe key files; nothing in the App changed yet.

---

## Phase 3: User Story 4 — The wizard stays read-only unless editing is allowed (P1)

**Goal**: the host/wizard seam exists and repair controls appear only when 010 allows editing; no write is ever caused by opening or paging the wizard.

**Independent Test**: edit mode off, unclean/unsupported fixture, write-protected and non-removable card doubles → no repair control, one hint line, zero writes (recording device).

- [X] T017 [US4] Implement `SshRepairHost` in `src/HddBackup.App/Forms/FilesTab.Edit.cs`: `CanRepair` = edit mode on with a usable session; `WhyNot` reuses the 010 refusal reason codes; raises `Changed` from `BeginEdit/EndEdit/LeaveEditMode/Save/Discard`; supplies `PendingTree` (image) or the rebuilt base tree (card); passes the host to `SshWizardDialog` in `OnToolsClick` (FilesTab.cs line ~221)
- [X] T018 [US4] Add to `src/HddBackup.App/Forms/SshWizardDialog.cs` the hint line and the (initially empty) repair button area; hide/disable repair controls on `Changed`; wizard constructor takes an optional `ISshRepairHost` (null = pure 009 behaviour)
- [X] T019 [P] [US4] Add strings `Repair_Hint_EditOff`, `Repair_Hint_Why_*` (one per 010 refusal reason) to all six `.resx`; `RepairTexts` in `src/HddBackup.App/Forms/RepairTexts.cs`
- [ ] T020 [US4] Tests `tests/HddBackup.Core.Tests/Repair/RepairGateTests.cs` (Core-level: the gate function mapping `EditSupport`/`DeviceInfo`/edit state → `CanRepair`/reason) and a test that building a report and a plan with a recording device performs zero writes and zero write-opens

**Checkpoint**: US4 acceptance scenarios 1–3 pass; the wizard is identical to 009 outside edit mode.

---

## Phase 4: User Story 1 — Add my public key so I can log in again (P1) 🎯 MVP

**Goal**: add a valid public key to the right account's key file, on an image (waits for Save) or on a card (at once), and see the verdict turn to "should work".

**Independent Test**: on `edit-ssh-nokey` add a key → Save → key line in the account's key file with mode 0600/700 and owner pi, `e2fsck`-clean, wizard re-run says "should work: pi with a key".

### Tests

- [X] T021 [P] [US1] `tests/HddBackup.Core.Tests/Repair/PublicKeyTests.cs`: good lines of every supported type (known keys with their `ssh-keygen -lf` fingerprints stored as constants), comment, options with quotes, legacy DSA warning; refusals: empty, private key (OpenSSH, PEM, PuTTY markers), certificate, two lines, bad base64, type/body mismatch, unknown type, line over 16 KiB; CRLF input trimmed
- [X] T022 [P] [US1] `tests/HddBackup.Core.Tests/Repair/SshRepairPlannerTests.cs` (add-key part): no `.ssh` → folder 700 + file 600 with the account's uid/gid in safe order; existing file with/without final newline, with CRLF and BOM → old bytes preserved, one line appended; duplicate (same body, different comment/options) → `Duplicate`; `AuthorizedKeysFile` custom pattern with `%h`/`%u`; key-command → `KeysFromCommand`; no home → `NoHome`; key file is a link leaving the file system → `OutsideFileSystem`; account with a link inside home followed
- [X] T023 [US1] `tests/HddBackup.Core.Tests/Repair/SshRepairEndToEndTests.cs` (add-key part): `edit-ssh-nokey` → plan → `AddRepair` → Save → export → `e2fsck -fn` clean; mode/owner as expected; original SHA-256 unchanged before Save; wizard check on the saved image says "should work"; card variant through `CardEditSession.Apply(list)` on the `rpi-like` card fixture with zero writes outside the partition
- [X] T024 [P] [US1] `tests/HddBackup.Core.Tests/Repair/RepairInterruptionTests.cs` (add-key part): interrupt the card apply at every write (fault device) → key file entirely old or entirely new, folder state harmless, checker clean

### Implementation

- [X] T025 [US1] Implement `src/HddBackup.Core/Repair/PublicKey.cs` (`TryParse`, `Refusal`, `Warning`, `Fingerprint`, `Line`; research R-8); T021 passes
- [X] T026 [US1] Implement `SshRepairPlanner.PlanAddKey(SystemReader, SshReport, accountName, PublicKey)` in `src/HddBackup.Core/Repair/SshRepairPlanner.cs`: resolves the key file (`Account.KeyFilePath`), reads current content in memory, duplicate check by type+body, builds the ordered `EditChange`s (create `.ssh` 700, create/replace file 600, owner) with the append rules of R-8, `ChangeLine` summary with fingerprint only; T022 passes
- [X] T027 [US1] Implement `src/HddBackup.Core/Repair/RepairLog.cs` and the host's `Apply` for add-key in `FilesTab.Edit.cs` (image: `AddRepair`; card: confirmation of 010 then `Apply(list)`; then refresh tree and re-run the check); T023/T024 pass
- [X] T028 [US1] Build `src/HddBackup.App/Forms/AddKeyDialog.cs` per contracts/ui.md (account combo, key box, **Load…** with ≤ 16 KiB `.pub` read, parsed facts row, target line, refusals/warnings, **Add**/**Cancel**; private key → clear box and message; string dropped on close) and wire **Add a key…** on the Result page and page 3 of `SshWizardDialog.cs`
- [X] T029 [P] [US1] Strings for `AddKeyDialog` and key refusals/warnings (`Repair_Key_*`, `Repair_Refused_*` for add-key) in all six `.resx`, texts in `RepairTexts.cs`
- [X] T030 [US1] `tests/HddBackup.Core.Tests/Repair/RepairSecretLeakTests.cs`: after planning and applying an add-key, scan `RepairLog`, titles, the report text and `ReportBuilder` output for the key body and for private-key markers (none); private key input leaves no object holding it

**Checkpoint**: MVP — a user can restore SSH access by key on an image or a card. SC-001, SC-006 testable.

---

## Phase 5: User Story 2 — Fix the problems the wizard found (P1)

**Goal**: **Fix…** for each repairable reason of research R-5, with an exact-change confirmation.

**Independent Test**: for each broken fixture press **Fix…**; verdict reaches "should work" (or the next independent reason); `e2fsck`-clean.

### Tests

- [X] T031 [P] [US2] `tests/HddBackup.Core.Tests/Repair/SshdEditTests.cs`: replace-in-place keeps the old line as a note comment; add/remove token in `AllowUsers`/`DenyUsers`/`AllowGroups`; default-only setting inserted before the first `Match` (never after), appended when no `Match`; drop-in rule (`Include` before position → `00-hddbackup.conf`); CRLF and no final newline; every other line byte-identical (property test over generated files)
- [X] T032 [P] [US2] `tests/HddBackup.Core.Tests/Repair/PasswdEditTests.cs`: only the shell field of one account changes; `/etc/shells` and file existence choose the shell; none suitable → `NoSuitableShell`; colon-containing GECOS untouched; other lines identical
- [X] T033 [P] [US2] Extend `SshRepairPlannerTests.cs`: one case per row of research R-5 (enable server from `[Install]`, alias link, unit without `[Install]` → `NoInstallSection`, unmask `/etc` link, `/lib` mask → text only, `AllowUsers`, `DenyUsers`, `AllowGroups` with group lookup, `PubkeyAuthentication no`, root login key-only first and `yes` as risk alternative, password-auth risk alternative, `NoShell`, unsafe modes with `SetAttributes` for home/.ssh/file), the `Match`-only restriction → `ListedInMatch`, and "never offered": empty passwords, hashes, host keys
- [X] T034 [US2] End-to-end per broken fixture in `SshRepairEndToEndTests.cs` (disabled, masked, allowusers, badshell, badmodes, dropin): Save → export → `e2fsck -fn`, link targets/modes/owners as expected, only expected files differ (byte-compare of all other files via the manifest), re-run check says "should work"; one run applies several repairs in a row and checks that the second builds on the first; interruption variant for the link and attribute repairs in `RepairInterruptionTests.cs`

### Implementation

- [X] T035 [P] [US2] Implement `src/HddBackup.Core/Repair/SshdEdit.cs` (line-preserving editor of R-7) using the `SettingValue.Source/Line` of `SshdConfig`; T031 passes
- [X] T036 [P] [US2] Implement `src/HddBackup.Core/Repair/PasswdEdit.cs`; T032 passes
- [X] T037 [US2] Implement `SshRepairPlanner.PlanFor(Finding/LoginReason, account)` returning the primary plan plus `Alternatives` (risk plans) or `Unavailable` with a `RepairRefusal`, for every row of R-5; unit location through `SystemdScan`; reads via `SystemReader` over the current tree; T033/T034 pass
- [X] T038 [US2] Host `Apply` generalised to any `RepairPlan` (stale check: plan rebuilt from the current tree must equal the shown one, else `Stale`); `RepairLog` entries for every kind
- [X] T039 [US2] Build `src/HddBackup.App/Forms/RepairConfirmDialog.cs` per contracts/ui.md (change list, risk paragraph + "I understand" check box, alternatives radio list with the key-only choice selected, card/image notes) and wire **Fix…** buttons on reasons (Result page) and on accounts with unsafe key files (page 3) in `SshWizardDialog.cs`
- [X] T040 [P] [US2] Strings `Repair_*`, `Repair_Why_*`, `Repair_Risk_*`, `Repair_Refused_*`, `Repair_Change_*` in all six `.resx`; texts in `RepairTexts.cs`; test that every `RepairKind`/`RepairRefusal`/`Refusal` value has a string in `tests/HddBackup.Core.Tests/Localization/` next to `ResourceParityTests`

**Checkpoint**: SC-002, SC-004, SC-005 testable. Every repairable reason in R-5 turns to "should work" on its fixture.

---

## Phase 6: User Story 3 — See, undo and understand the repairs before they take effect (P2)

**Goal**: waiting repairs on an image are listed in words, undoable one by one, and the wizard shows the verdict "after these repairs".

**Independent Test**: queue two repairs, undo one, Save → only the other is in the image; the original file is unchanged until Save.

- [X] T041 [US3] Implement the waiting-repairs block in `SshWizardDialog.cs` per contracts/ui.md: list in words from `Waiting`, **Undo…** menu calling `host.Undo`, "After these repairs the verdict will be: …", "(waiting for Save)" marks on reasons resolved only by waiting repairs; hidden on cards
- [X] T042 [US3] Host `Undo` + `Changed` plumbing in `FilesTab.Edit.cs`: refresh `PendingTree`, tree and status count; refused undo (later hand edit touched the path) shows the explanation; wizard re-check on a worker thread with stale-result dropping (R-10)
- [X] T043 [P] [US3] Report section "Repairs in this session" in `src/HddBackup.Core/Diagnostics/ReportBuilder.cs` + `src/HddBackup.App/Forms/ReportTexts.cs` (words, "waiting for Save"/"saved"/"written", fingerprints only); test in `tests/HddBackup.Core.Tests/Diagnostics/ReportBuilderTests.cs` and `RepairSecretLeakTests.cs`
- [X] T044 [P] [US3] Strings `Repair_Waiting`, `Repair_AfterVerdict`, `Repair_Undo*`, `Repair_Report_*` in all six `.resx`
- [ ] T045 [US3] Tests in `SshRepairEndToEndTests.cs`: queue add-key + enable-server, undo the key, Save, only the link exists; hand edit of the same file after a repair → undo refused; closing and reopening the image keeps groups (diff v2); card path has no Undo

**Checkpoint**: all four stories work and are independently testable.

---

## Phase 7: Polish & cross-cutting

- [X] T046 [P] Update `docs/manual-hardware-checklist.md` (rows 49–52 from quickstart "Manual checks"), `README.md` (SSH check section: repairs and keys), `docs/release-notes.md` (Unreleased), `docs/safety-review.md` (writer additions, secrets, risk repairs)
- [X] T047 Language sweep: open both new dialogs and the Result page in all six languages at 100 % and 150 % DPI, fix clipping; screenshot for README
- [X] T048 [P] Add the repair exports to `tools/ci/Invoke-Ext4Checks.sh` and check the `ext4-check` job passes locally under Docker; run `sshd -T -f` once on a saved fixture tree (quickstart oracle) and record the result in `research.md` R-11
- [X] T049 Timing: measure planning and re-check on `E:\ZTR\water\good-modified.img` (env-gated test like `LargeImageTimingTests`) and a card repair preflight on the same image; record in `research.md`
- [ ] T050 Full Release build and test run (0 warnings, all pass), then the quickstart manual checks 1–4 on a real image and card; update the spec **Status** line; tick `checklists/requirements.md` notes

---

## Dependencies & Execution Order

- Phase 1 (T001 gates App tasks only; T002–T004 gate fixture-based tests) → Phase 2 (T005–T016) → Phase 3 (US4) → Phase 4 (US1) → Phase 5 (US2) → Phase 6 (US3) → Phase 7.
- Inside Phase 2: T005/T006/T013 (tests) first; T007 → T008 → T009; T010 needs T007; T011 needs T007–T010; T012 needs T007–T008; T014 needs T013; T015/T016 independent.
- US1 needs US4's seam (T017–T018) and Phase 2; US2 needs US1's host `Apply` (T027) and planner skeleton (T026); US3 needs US1/US2 host and `AddRepair/Undo` (T011).
- US2's Core work (T031–T037) can start right after Phase 2, in parallel with US1's App work.

### Parallel opportunities

- T002 ∥ T003; T005 ∥ T006 ∥ T013 ∥ T016.
- After Phase 2: T021 ∥ T022 ∥ T024; T031 ∥ T032 ∥ T033; T035 ∥ T036; all string tasks (T019, T029, T040, T044) ∥ code tasks.
- T046 ∥ T048.

## Implementation Strategy

1. **MVP** = Phases 1–4: key adding works on image and card with the read-only gate. Stop and verify
   with `quickstart.md` before continuing.
2. Add US2 (fixes) next; it reuses the host `Apply`, dialogs and fixtures of the MVP.
3. US3 (undo and "after repairs" verdict) last; the repairs are already safe without it.
4. After each phase run the full suite; the 009 and 010 tests must keep passing at every checkpoint.

---

## Implementation notes (deviations from the plan, found while building the Core part)

- **T002/T003**: the fixtures are made by a new script `tests/fixtures/ext4/make-ssh-repair-fixtures.sh` (not an extension of
  `make-editing-fixtures.sh`, which has no helpers for Debian-like roots). `check-editing.sh` compares mode, owner and link target
  of every entry of the `ssh-*` scenarios with `debugfs stat`; the tests export `saved-<scenario>.attrs.tsv` for it. Verified once
  under Docker (real `e2fsck -fn` and `debugfs`): all ten `ssh-*` exports are clean and identical.
- **Research R-7 drop-in rule dropped**: a setting that is only a default is added to the main file before the first `Match`
  line. That is always read, because a setting that exists in any drop-in is edited where it stands. List keywords
  (`AllowUsers`, `DenyUsers`, `AllowGroups`, `DenyGroups`) add up across lines in sshd, so "allow" adds a line after the first
  one and "unblock" removes the exact word from the line(s) it is on (`SshSettings.ListTokens`).
- **`SystemReader.ReadBytes`** was added so appending a key and editing text files is byte exact (files are handled as Latin-1).
- **Root offers**: the planner offers nothing for `root` while another account can log in (a root that cannot log in is the usual
  state), and offers key-only root login (never password) first when root login is switched off.
- **StrictModes**: root-owned key files are accepted (as the server does); a `.ssh` folder with mode 755 is accepted.
- **Done in Core, waiting for the layout approval (T001) in the App**: T015, T017–T020, T027 (host part), T028, T029, T038–T044,
  T045 (UI part), T047 and the rest of Phase 7.

### Status after the UI work

- **Done**: all tasks except T020, T045 and T050's manual part.
- **T020** is covered differently: repair controls exist only while `ISshRepairHost.CanRepair` (edit mode with a usable session), and
  the wizard reads and plans through the read-only `DiagnosticsRunner` handle, so opening it cannot write; there is no separate
  Core gate function to test. When edit mode is off the wizard shows the hint `Repair_Hint_EditOff` instead of the 010 refusal reason
  (the reason would need opening an edit session just to ask).
- **T045** (UI-level): the Core behaviour (groups survive reopening, undo refused after a later change, card has no undo) is tested in
  `RepairGroupTests` and `SshRepairEndToEndTests`; the dialogs were checked with screenshots in all six languages (a temporary harness,
  not part of the repository); a hand run with a real image and card is checklist rows 49–52.
- **T047**: all three dialogs and the Result and Accounts pages were rendered in en, uk, de, es, fr and ja without clipping. 150 % DPI
  and Windows 7 remain for the hand check (row 52).
- **T048**: the repair exports are picked up by `tools/ci/Invoke-Ext4Checks.sh` through `check-editing.sh` unchanged; run once under
  Docker locally with the real `e2fsck -fn` and `debugfs stat` (all clean). `sshd -T` was not run.
- **Wizard shows the state after Save**: instead of a separate "after these repairs" verdict, the whole wizard is read on the file system
  with the waiting repairs laid over it and says so in the banner and above the list of waiting repairs.

