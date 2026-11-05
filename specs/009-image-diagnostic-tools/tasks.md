---

description: "Task list for feature 009: diagnostic tools for the opened Linux file system (SSH check, startup and scheduled tasks)"
---

# Tasks: Diagnostic Tools for the Opened Linux File System

**Input**: Design documents from `/specs/009-image-diagnostic-tools/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md

**Tests**: Included — constitution Principle VI requires test-verified logic, Principle I needs a
test for every safety rule (read-only, no secrets), and the success criteria (SC-001, SC-002, SC-004,
SC-005) are defined against prepared configurations and an independent Linux check.

**Organization**: US1 SSH check wizard (P1), US2 startup and scheduled tasks (P1), US3 keep the
result (P2), US4 sources that are not what the tools expect (P3).

## Format: `[ID] [P?] [Story] Description`

- Paths: `src/HddBackup.Core/Diagnostics/`, `src/HddBackup.App/Forms/`,
  `tests/HddBackup.Core.Tests/Diagnostics/`, `tests/fixtures/ext4/`, `tools/ci/`
- Strings: every new key goes into all six `src/HddBackup.App/Resources/Strings{,.uk,.de,.fr,.es,.ja}.resx`
  (English texts in `contracts/ui.md` and the finding table of `data-model.md`). The existing
  helper used for feature 008 appends keys to the six files from one JSON map; the resource-parity
  test must pass. **Never pipe the helper's output into `Select-Object -First`: it stops the
  script early.**
- Core results are language-neutral (codes and arguments); texts are built in the App (research R-10).
- No result type may hold a secret (research R-6); parsers reduce passwords and key lines to facts.
- The constitution amendment 1.4.0 is already applied (2026-11-05).

---

## Phase 1: Setup — gates and fixtures

- [X] T001 Get the draft layout mockup in `specs/009-image-diagnostic-tools/spec.md` approved by the maintainer; change its heading to `(approved)` and the **Status** line to `Draft (layout approved <date>)`. **Gate: no UI task (T014, T029, T030, T044, T045, T049, T053) starts before this.**
- [X] T002 Create `tests/fixtures/ext4/make-diagnostic-fixtures.sh` (model: `make-browse-fixtures.sh`, helpers in `fixture-lib.sh`) building small bare ext4 roots (16 MiB, 4 KiB blocks, `metadata_csum`) with `mke2fs -d` from staging trees, written to `tests/HddBackup.Core.Tests/Fixtures/ext4/` as `<name>.img.gz`: `diag-ssh-ok` (OpenSSH-like `/etc/ssh/sshd_config` with an `Include /etc/ssh/sshd_config.d/*.conf` and one drop-in, `/usr/sbin/sshd` file, `/lib/systemd/system/ssh.service` and `ssh.socket`, link `/etc/systemd/system/multi-user.target.wants/ssh.service` → `/lib/systemd/system/ssh.service`, `/etc/systemd/system/multi-user.target` default link, passwd/shadow/group with `root` (locked) and `pi` (hash set) and an `nologin` system account, `/home/pi/.ssh/authorized_keys` with two valid key lines), `diag-ssh-keys-only` (`PasswordAuthentication no`, `PermitRootLogin no`, key for `pi`, a second user `guest` with locked password and no key), `diag-ssh-disabled` (server installed, no start link), `diag-startup` (≥ 40 entries: enabled services and timers in `multi-user.target.wants` and `timers.target.wants`, a masked unit linked to `/dev/null`, a service enabled only in an unreached target, a user unit, executable `/etc/rc.local`, `/etc/xdg/autostart` and `~/.config/autostart` `.desktop` files (one `Hidden=true`), `/etc/xdg/lxsession/LXDE-pi/autostart`, `/etc/crontab` with the four `run-parts` lines, `/etc/cron.d/` files (one with a dot in its name), user crontabs in `/var/spool/cron/crontabs/` (one for a missing user), periodic scripts (one not executable, one with a name containing a dot), `@reboot` and `@daily` lines, `/usr/sbin/cron`), and `diag-pi-like` (an MBR image: FAT16 boot partition plus ext4 root; `/etc/os-release` with `ID=raspbian`, `/lib/systemd/system/sshswitch.service`, SSH installed and not enabled). Plant secrets in all of them: `$6$PLANTEDSECRET$…` hashes, `PLANTEDKEYBLOB` inside valid-looking key lines, and a `-----BEGIN OPENSSH PRIVATE KEY-----` file in `/home/pi/.ssh/id_ed25519`. Write beside each fixture an `<name>.expect.tsv` where the independent oracles can supply it (T004)
- [X] T003 Run `make-diagnostic-fixtures.sh` in Docker (command in `quickstart.md`), check the fixtures into the repository and confirm `FixtureImages.RawPath("diag-startup")` works without code changes (single-partition images open through `BrowseSource`; `diag-pi-like` is a whole-device image)
- [X] T004 Create `tests/fixtures/ext4/check-diagnostics.sh`: for each fixture listing exported by the tests (`<exports>/diagnostics/<fixture>.ssh.tsv` = `keyword<TAB>value`, `<fixture>.units.tsv` = enabled unit names) run, in the disposable container, `ssh-keygen -A`, copy the fixture's `/etc/ssh` over `/etc/ssh`, `sshd -T` (no `-C`, so `Match` blocks are not evaluated) and compare the keywords `port`, `permitrootlogin`, `passwordauthentication`, `kbdinteractiveauthentication`, `usepam`, `pubkeyauthentication`, `permitemptypasswords`, `allowusers`, `denyusers`, `allowgroups`, `denygroups`, `authorizedkeysfile`; and `systemctl --root=<fixture root> list-unit-files --state=enabled --no-legend` against the units listing; exit non-zero on any difference. Hook it into `tools/ci/Invoke-Ext4Checks.sh` (after the extracted-tree loop; no-op without `diagnostics/`) and install `openssh-server` and `systemd` in the `ext4-check` job of `.github/workflows/ci.yml`

---

## Phase 2: Foundational — reader, shared types, runner, Tools button

- [X] T005 [P] Create `src/HddBackup.Core/Diagnostics/WildcardMatcher.cs`: `internal static class WildcardMatcher` with `bool IsMatch(string pattern, string text)` for `*`, `?` and `[abc]`/`[a-z]`/`[!x]` (case-sensitive), used for `Include`, drop-in directories and `AllowUsers` patterns
- [X] T006 [P] Create `tests/HddBackup.Core.Tests/Diagnostics/WildcardMatcherTests.cs`: table-driven cases (`*.conf`, `50-?.conf`, `[0-9]*`, no match across `/`, empty pattern, escaped-looking characters)
- [X] T007 Create `src/HddBackup.Core/Diagnostics/SystemReader.cs` per research R-1 and data-model.md: `public sealed class SystemReader` over `IFileTree` with `Exists`, `Stat`, `ReadText(path, maxBytes = 1 MiB)` (UTF-8, truncation flag), `ReadLines`, `List(path, max = 5000)` (cached, sorted ordinal), `ReadLink`, `Resolve(path, followLast)` (absolute and relative targets, `..`, ≤ 40 links, loops → null), caches for listings and reads, and honours a `CancellationToken`; unreadable entries and read errors become return values (`null` / flags), only source device errors (`ImagingException` `DeviceRemoved`/`ReadError`) propagate; no write path
- [X] T008 [P] Create `tests/HddBackup.Core.Tests/Fakes/LinuxRootBuilder.cs`: builds an in-memory Linux root on `FakeFileTree` from path-based calls — `File(path, text, mode = 0644)`, `Dir(path)`, `Link(path, target)`, `Executable(path, text)`, `Passwd/Shadow/Group` helpers, `OsRelease(id)` — creating parents, with a `Reader()` returning a `SystemReader`; extend `FakeFileTree` only if links need a resolution hook (it already stores `LinkTarget`)
- [X] T009 [P] Create `tests/HddBackup.Core.Tests/Diagnostics/SystemReaderTests.cs`: reads text and truncates at the limit; lists sorted and caps at 5000; follows an absolute link (`/etc/systemd/system/multi-user.target.wants/ssh.service` → `/lib/systemd/system/ssh.service`), a relative link with `..`, a chain of links, merged-`/usr` (`/lib` → `usr/lib`), a link to a missing target (`Exists` false), a loop (null, no hang), a link to `/dev/null`; caches (second read does not touch the tree: count reads on the fake); cancellation; an unreadable entry is a value, not an exception; the same tests on the real `diag-startup` fixture
- [X] T010 [P] Create `src/HddBackup.Core/Diagnostics/DiagnosticsTypes.cs`: `public sealed class DiagnosticsSource` (`ImagePath`/`Device`, `PartitionNumber`, `DisplayName`, `State`, `HasUnreadBootPartition`), `public enum` `StartupKind`, `InactiveReason`, `FindingStatus`, `FindingCode`, `VerdictCode`, `LoginAnswer`, `PasswordState`, `ScheduleClass` and the small value types of data-model.md that several stories share (`SettingValue<T>`)
- [X] T011 Create `src/HddBackup.Core/Diagnostics/DiagnosticsRunner.cs` and `src/HddBackup.Core/Diagnostics/LinuxRoot.cs`: `DiagnosticsRunner.Run<T>(IDeviceAccess access, DiagnosticsSource source, Func<SystemReader, FsContext, CancellationToken, T> tool, CancellationToken ct)` opens its own read-only `BrowseSource` (image or device) and the partition's `Ext4FileSystemView`, wraps it in a `SystemReader`, runs the tool and disposes everything even on error; `LinuxRoot.Detect(reader)` implements research R-3 (`/etc/passwd` plus one of `/etc/os-release`, `/usr/lib/os-release`, `/usr/bin`, `/bin`) and returns the distribution `ID`/`PRETTY_NAME` and whether it is Raspberry Pi OS; never calls `OpenExclusiveForWrite`
- [X] T012 [P] Create `tests/HddBackup.Core.Tests/Diagnostics/DiagnosticsRunnerTests.cs`: runs a trivial tool on an image and on a `FakeDeviceAccess` device; the source is disposed after success, failure and cancellation (`ReadReleases == ReadOpens`); `LinuxRoot.Detect` on `diag-startup` (yes), `browse-large` (no) and an empty ext4; Raspberry Pi OS detected from `os-release`; `ReadOnlyBlockDevice` records zero writes and the image SHA-256 is unchanged (starts SC-005)
- [X] T013 Add the foundational strings to all six `Strings*.resx`: `Tools_Button`, `Tools_Ssh`, `Tools_Startup`, `Tooltip_Tools`, `Tools_Analysing`, `Tools_NoFileSystem` ("Open a file system first."), `Tools_Busy` ("An extraction or another tool is running.")
- [X] T014 Edit `src/HddBackup.App/Forms/FilesTab.cs` and `src/HddBackup.App/Forms/MainForm.cs`: a **Tools ▾** `Button` left of **Extract…** with a `ContextMenuStrip` (items added by the stories), enabled only when a file system is open, no extraction runs and no tool runs (tooltip says why otherwise); a `RunTool<T>(Func<…> tool, Action<T> show)` helper that builds the `DiagnosticsSource` from the tab's state (`_openedPath` or `_openedDevice`, the selected partition, `BrowseSource.Partitions` for `HasUnreadBootPartition`), shows `Tools_Analysing` with the tab's cancel state, runs `DiagnosticsRunner` on a worker thread, and shows read errors with `Files_ReadError`; pass `IDeviceAccess` (already in the tab) to the runner
- [X] T015 Create `src/HddBackup.Core/Diagnostics/SystemdScan.cs` per research R-4 and R-8: unit lookup (`/etc/systemd/system`, `/run/systemd/system`, `/usr/lib/systemd/system`, `/lib/systemd/system`), unit file parser (`[Unit]`, `[Service]`, `[Install]`, `[Timer]`: `Description`, `ExecStart` with prefix stripping, `User`, `Wants`, `Requires`, `Requisite`, `WantedBy`, `Also`, `OnCalendar`, `OnBootSec`, `Unit`), masked detection (link to `/dev/null`), alias links, `.wants`/`.requires` enumeration, default-target resolution and the **reached targets** set (walks targets only), `EnabledState(unit)` (`Yes`, `No`, `Masked`, `OnDemand`, `Legacy`, with evidence), enabled units per target with the unit's kind, system and per-user (`~/.config/systemd/user`) scopes, SysV fallback (`/etc/rc?.d/S??*`, `/etc/init.d`) only when `/lib/systemd/systemd` is absent
- [X] T016 [P] Create `tests/HddBackup.Core.Tests/Diagnostics/SystemdScanTests.cs` on `LinuxRootBuilder`: enabled via `multi-user.target.wants`; not enabled; masked (`/dev/null`); alias (`sshd.service` → `ssh.service`); enabled only in `graphical.target` while the default is `multi-user.target` (inactive: target not reached) and active when the default is `graphical.target` that wants `multi-user.target`; socket activation; unit file missing behind a link; template instance; timer with its `Unit=`; per-user unit; SysV-only system; `ExecStart` prefixes `-@+!` stripped; unreadable unit file → entry marked, scan continues

**Checkpoint**: the reader, the runner, the Tools button and the systemd scan work; the menu has no items yet.

---

## Phase 3: User Story 1 - SSH check wizard (Priority: P1) 🎯 MVP

**Goal**: **Tools → SSH check…** ends in a verdict and reasons for the opened file system.

**Independent Test**: the ≥ 12 configuration matrix and the real fixtures give the expected verdicts
and reasons; no secret appears in any result.

- [X] T017 [US1] Create `src/HddBackup.Core/Diagnostics/SshdConfig.cs` per research R-5: parser for `/etc/ssh/sshd_config` (comments, blank lines, case-insensitive keywords, blank or `=` separators, quoted arguments), `Include` expanded in place (relative to `/etc/ssh`, patterns via `WildcardMatcher`, sorted, depth ≤ 16), **first value wins**, accumulating keywords (`Port`, `ListenAddress`, `AuthorizedKeysFile`, `AllowUsers`, `DenyUsers`, `AllowGroups`, `DenyGroups`), `Match` blocks recorded as conditional (never applied), defaults table of R-5, `SettingValue<T>` with `Source` (file and line, or default) and `Conditional`, the alias `ChallengeResponseAuthentication`, `PermitRootLogin` values, `AuthenticationMethods`, `AuthorizedKeysCommand`; result type `SshSettings` of data-model.md
- [X] T018 [P] [US1] Create `tests/HddBackup.Core.Tests/Diagnostics/SshdConfigTests.cs`: first wins over a later duplicate; Include in the middle (value before and after); glob and sorted order; relative Include; missing Include target; Match block settings are conditional and not applied; defaults with source "default"; quoting and `=`; case-insensitive keywords; comments after values are not comments (`#` only at line start per sshd) — compare with `sshd -T` behaviour documented in research; a 1 MiB file is truncated with the flag; the alias keyword; export of the effective view to `<exports>/diagnostics/<fixture>.ssh.tsv` for the CI oracle (T004) when `HDDBACKUP_EXPORT_DIR` is set, for the three `diag-ssh-*` fixtures
- [X] T019 [US1] Create `src/HddBackup.Core/Diagnostics/AccountDatabase.cs` per research R-6: parse `/etc/passwd`, `/etc/group`, `/etc/shadow` into `Account` (data-model.md) where the shadow password field is turned into `PasswordState` (`Set`, `Locked`, `NoPassword`, `Empty`, `Unknown`) **inside the parser and not kept**; `HasUsableShell` (existing shell not `nologin`/`false`); `HasExpiry`; authorized keys read from the files named by `AuthorizedKeysFile` (tokens `%h`, `%u`, `%%`, relative to the home) and **only counted** (options prefix, known key types `ssh-ed25519`, `ssh-rsa`, `ecdsa-sha2-*`, `sk-*`, base64 blob); group membership including the primary group; unreadable or missing files → `Unknown`/`KeysUnknown`
- [X] T020 [P] [US1] Create `tests/HddBackup.Core.Tests/Diagnostics/AccountDatabaseTests.cs`: every shadow state (`$6$…`, `!`, `!!`, `!$6$…`, `*`, empty, no entry, shadow missing); shells; `%h/.ssh/authorized_keys` and a custom `AuthorizedKeysFile` with `%u`; options-prefixed key lines, comments, blank lines, junk lines not counted; two keys → 2; group lookups; **no property or `ToString()` of any result contains the planted hash or key blob** (reflection over the object graph)
- [X] T021 [US1] Create `src/HddBackup.Core/Diagnostics/SshReport.cs`: `Finding` (code, status, args, evidence, page), `Verdict` (code, users with method, reasons, notes), `SshReport` (state of the source, findings by page, accounts, verdict) exactly per data-model.md; no free-text secret field
- [X] T022 [US1] Create `src/HddBackup.Core/Diagnostics/SshCheck.cs` per research R-4 and R-7: `static SshReport Run(SystemReader reader, DiagnosticsSource source, CancellationToken ct)` — server installed/enabled/masked/on demand/legacy with evidence (via `SystemdScan`), the first-boot switch note (`sshswitch.service` present, or Raspberry Pi OS with a FAT partition in `source.HasUnreadBootPartition`), settings findings with their sources, accounts (shown set: root and uid ≥ 1000 except `nobody`; system accounts flagged), per-account login answers and reason codes (shell, locked with `UsePAM` rule, no password and no key, root rules of `PermitRootLogin`, `AllowUsers`/`DenyUsers`/`AllowGroups`/`DenyGroups` with `*`, `?`, `!` and `user@host` → conditional, empty password with `PermitEmptyPasswords`, password/key auth off, `AuthenticationMethods` both), conditional `Match` notes, `AuthorizedKeysCommand` note, and the verdict in the order of R-7 (`NoServer`, `NotEnabled`/`NotEnabledBootFlagPossible`, `NobodyCanLogIn`, `Works`, `Undetermined` only when it changes the answer); `NotALinuxRoot` when `LinuxRoot.Detect` fails
- [X] T023 [P] [US1] Create `tests/HddBackup.Core.Tests/Diagnostics/SshCheckMatrixTests.cs` (SC-001): a table of **≥ 12 configurations** built with `LinuxRootBuilder`, each with the expected verdict code, the list of accounts that can log in (and by what), and the expected reason codes: (1) enabled, password and key, regular user; (2) enabled, keys only with a key; (3) keys only, no key anywhere → `NobodyCanLogIn`; (4) server not enabled; (5) masked; (6) no server; (7) `PermitRootLogin no` and only root has a key; (8) root with key and `prohibit-password`; (9) locked user with `UsePAM no` (refused) and with `UsePAM yes` + key (works); (10) empty password refused / allowed with `PermitEmptyPasswords yes`; (11) `AllowUsers` excludes the only user; (12) `DenyGroups`; (13) settings from an `Include` drop-in overriding nothing (first wins); (14) `Match` block that flips password auth → note, verdict `Works` with the conditional note; (15) `AuthorizedKeysCommand` → `Undetermined` only when no key exists elsewhere; (16) socket activation; (17) Raspberry Pi OS, not enabled, FAT partition flag set → `NotEnabledBootFlagPossible`; (18) no shadow readable → note; (19) no regular account on Raspberry Pi OS; (20) not a Linux root. Reasons are asserted in fix-first order
- [X] T024 [P] [US1] Create `tests/HddBackup.Core.Tests/Diagnostics/SshFixtureTests.cs`: run `SshCheck` through `DiagnosticsRunner` on `diag-ssh-ok`, `diag-ssh-keys-only`, `diag-ssh-disabled` and `diag-pi-like` (a whole-device image: `HasUnreadBootPartition` true) and assert the verdicts of quickstart scenarios 2–5; assert the chain works on real symbolic links and modes; image SHA-256 unchanged
- [X] T025 [P] [US1] Create `tests/HddBackup.Core.Tests/Diagnostics/SecretLeakTests.cs` (SC-004), SSH part: for every `diag-*` fixture and every `LinuxRootBuilder` matrix case, serialize the whole `SshReport` (reflection over all public properties, `ToString()`) and assert that none of `PLANTEDSECRET`, `PLANTEDKEYBLOB`, `BEGIN OPENSSH PRIVATE KEY` or the planted hash occurs; the private key file is never opened (the fake tree records the paths read: `/home/pi/.ssh/id_*` must not appear)
- [X] T026 [US1] Add the SSH strings to all six `Strings*.resx` (one JSON map, English in `contracts/ui.md` and `data-model.md`): wizard chrome (`Ssh_Title`, `Ssh_Step1..4`, `Ssh_Back`, `Ssh_Next`, `Ssh_NotLinux`, `Ssh_ShowSystemAccounts`, `Ssh_Why`, `Ssh_CouldNotCheck`, `Ssh_SettingFrom`, `Ssh_FromDefault`), account columns and values (`Ssh_Col*`, `Ssh_Pw_*`, `Ssh_With_*`), account reasons `Ssh_R_*`, and the verdicts `Ssh_V_*` (all six codes)
- [X] T027 [US1] Add the SSH finding strings `Ssh_F_<Code>` (every `FindingCode` of the table in `data-model.md`) to all six `Strings*.resx`, with the longer explanations also available as tooltips where the page uses them
- [X] T028 [US1] Create `src/HddBackup.App/Forms/DiagnosticTexts.cs` (SSH part): `string Finding(Finding f)`, `string Verdict(Verdict v)`, `string Reason(code, args)`, `string Account columns` helpers using `Strings.Format` with the arguments of the Core result, `Formatters` for sizes where needed; every enum value has a key (a resource-parity test requires it)
- [X] T029 [US1] Create `src/HddBackup.App/Forms/SshWizardDialog.cs` per `contracts/ui.md`: modal `Form` with the step header, four pages (labels with ✔ ✖ ⚠ ℹ glyphs and grey evidence paths; a `ListView` for accounts with the "Show system accounts" check box; the result page with the verdict banner, "Why" and "Could not check"), **◂ Back / Next ▸ / Close**, Enter/Esc, the not-cleanly-unmounted warning under the header, not-a-Linux-root page with only **Close**, re-render on language change; no control changes the image
- [X] T030 [US1] Edit `src/HddBackup.App/Forms/FilesTab.cs`: add the **SSH check…** menu item calling `RunTool` with `SshCheck.Run` and showing `SshWizardDialog`; add `Ssh*` handling to `ApplyStrings`
- [X] T031 [US1] Run T006, T009, T012, T016, T018, T020, T023, T024, T025 and fix T005–T030 until they pass; run the whole test project
- [X] T032 [US1] Manual check of quickstart scenarios 1–5 and 12 with the scripted harness (render the wizard pages on `diag-ssh-ok`, `diag-ssh-keys-only`, `diag-ssh-disabled`, `diag-pi-like` in `en` and one other language) and fix

**Checkpoint**: the SSH check works end to end; MVP of the diagnostics.

---

## Phase 4: User Story 2 - Startup and scheduled tasks (Priority: P1)

**Goal**: **Tools → Startup and scheduled tasks…** lists everything that starts on its own.

**Independent Test**: the reference system of ≥ 40 entries gives exactly the expected active list,
schedules in words, users and sources; inactive entries appear only on request.

- [X] T033 [P] [US2] Create `src/HddBackup.Core/Diagnostics/CronSchedule.cs` per research R-9: `CronSchedule.Parse(five fields or shortcut)` into sets (ranges, steps, lists, month and weekday names, `7` = Sunday, `*`), classification into `ScheduleClass` (`AtBoot`, `EveryMinute`, `EveryNMinutes`, `HourlyAt`, `DailyAt` with ≤ 4 times, `WeeklyOn`, `MonthlyOn`, `YearlyOn`, `Custom`; both day fields restricted → `Custom`), `Raw`, malformed → null; a small `TimerCalendar.Parse` for the common `OnCalendar` forms (`daily`, `weekly`, `hourly`, `*-*-* HH:MM[:SS]`) mapping to the same classes, others `Custom`
- [X] T034 [P] [US2] Create `tests/HddBackup.Core.Tests/Diagnostics/CronScheduleTests.cs`: every class with several spellings (`*/5 * * * *`, `0 * * * *`, `25 6 * * *`, `25 6,18 * * *`, `0 3 * * mon`, `0 3 * * 1-5`, `30 2 1 * *`, `0 0 25 12 *`, `@reboot`, `@daily`, `@hourly`, `@midnight`, `@weekly`, `@monthly`, `@yearly`/`@annually`); steps and ranges; names case-insensitive; `7` as Sunday; invalid fields, out-of-range numbers and wrong field counts → null; day-of-month and day-of-week together → `Custom`; `OnCalendar` forms
- [X] T035 [US2] Create `src/HddBackup.Core/Diagnostics/CronScan.cs` per research R-9: `/etc/crontab` and `/etc/cron.d/*` (system format with user; Debian name rule `^[A-Za-z0-9_-]+$`, other names → inactive `IgnoredName`), `/var/spool/cron/crontabs/<user>` and `/var/spool/cron/<user>` (user format, file name = user, nonexistent user → `NoSuchUser`), environment lines skipped, `@reboot` and shortcuts, `/etc/anacrontab`, periodic directories `/etc/cron.hourly|daily|weekly|monthly` (executable regular files or links to them with `run-parts` names; schedule taken from the `run-parts` line of `/etc/crontab` or anacron; none → inactive `NothingRunsIt`; not executable → `NotExecutable`; ignored name → `IgnoredName`), malformed lines → inactive `Malformed`, cron program absent (`/usr/sbin/cron`, `/usr/sbin/crond`) → all inactive `NoCron`, installed but service not enabled → note only; produces `StartupEntry` items of kind `Cron`
- [X] T036 [P] [US2] Create `tests/HddBackup.Core.Tests/Diagnostics/CronScanTests.cs` on `LinuxRootBuilder`: system crontab with user field, `cron.d` file with a dot in its name ignored, user crontabs (one for a missing user), `@reboot`/`@daily`, environment lines, comments not listed, periodic scripts (executable, not executable, dotted name, link), schedule of a periodic script from the `/etc/crontab` `run-parts` line ("every day at 06:25"), anacron entry, malformed line, no cron installed, cron service not enabled (note), very long crontab (cap), non-ASCII command
- [X] T037 [US2] Create `src/HddBackup.Core/Diagnostics/AutostartScan.cs` per research R-8: `/etc/rc.local` (executable, non-comment lines, one entry per command), XDG autostart (`/etc/xdg/autostart/*.desktop` and `~/.config/autostart/*.desktop` per account; the user file overrides the system one by name; `Hidden=true`, `X-GNOME-Autostart-enabled=false`, `OnlyShowIn` excluding the session → inactive `HiddenEntry`; `Exec=` field codes stripped), Raspberry Pi OS lxsession and labwc autostart files (one entry per non-comment line; leading `@` marks the program), SysV links when relevant (delegated to `SystemdScan`)
- [X] T038 [P] [US2] Create `tests/HddBackup.Core.Tests/Diagnostics/AutostartScanTests.cs`: `rc.local` executable and not executable; `.desktop` with `Hidden=true`; a user file overriding a system one; `OnlyShowIn`; field codes `%U %f` stripped; LXDE `@lxpanel` lines and comments; labwc autostart; accounts without a home
- [X] T039 [US2] Create `src/HddBackup.Core/Diagnostics/StartupInventory.cs`: `static StartupResult Run(SystemReader reader, DiagnosticsSource source, CancellationToken ct)` merging `SystemdScan` (services, timers, sockets, user units), `AutostartScan` and `CronScan` into `StartupEntry` items (data-model.md) with kind, name, command, schedule (`CronSchedule` or `AtBoot`) and original text, user, source, `Active`/`InactiveReason`, evidence; active first, then sorted by kind and name; counts per kind and active/inactive; notes (`NoCron`, `CronNotEnabled`, `ListCut`, `Truncated`); one unreadable source marks its entries and does not stop the scan; `NotALinuxRoot` handling like the SSH check
- [X] T040 [P] [US2] Create `tests/HddBackup.Core.Tests/Diagnostics/StartupInventoryTests.cs` (SC-002): a reference system built with `LinuxRootBuilder` holding **≥ 40 expected entries** (services, timers, sockets, boot script lines, desktop and LXDE entries, system/user cron, periodic scripts, `@reboot`) with their expected kind, name, command, schedule class, user, source; assert the active list equals the expected set exactly (each once), inactive entries are absent from it and present with the right reason when requested; plus `diag-startup` through `DiagnosticsRunner`; export the enabled units to `<exports>/diagnostics/diag-startup.units.tsv` for the CI oracle (T004)
- [X] T041 [P] [US2] Extend `tests/HddBackup.Core.Tests/Diagnostics/SecretLeakTests.cs` (SC-004) to `StartupResult` for every fixture and matrix case: no planted secret and no private-key marker in any property or `ToString()`; commands are data from the image and are checked to be exactly the planted command text, not more
- [X] T042 [US2] Add the startup strings to all six `Strings*.resx`: `Startup_*` (title, filter, kinds, search, only-active, counts, columns, status), `Startup_R_*` (all twelve reasons), `Startup_N_*` (notes), and the schedule words `Sched_*` (all classes and `Sched_TimeList`)
- [X] T043 [US2] Extend `src/HddBackup.App/Forms/DiagnosticTexts.cs` with the startup part and create `src/HddBackup.App/Forms/ScheduleWords.cs`: `string Format(CronSchedule s)` using the `Sched_*` resources, weekday and month names from `LanguageService.Culture.DateTimeFormat`, times in the culture's short time pattern, multiple times joined with `Sched_TimeList`, `Custom` shows the raw text; unit tests cannot run in the Core test project, so add an App-free table of expected strings to `docs`-style comments and verify through the harness (T046)
- [X] T044 [US2] Create `src/HddBackup.App/Forms/StartupTasksWindow.cs` per `contracts/ui.md`: resizable modal `Form` with the kind `ComboBox`, search `TextBox`, "Only what is active" check box (default on), `ListView` in details mode with the six columns, column-header sorting, the schedule in words with the original text as tooltip, inactive rows grey with the reason in **Status**, the count line, the notes below the list, the not-cleanly-unmounted warning, the not-a-Linux-root message, re-render on language change
- [X] T045 [US2] Edit `src/HddBackup.App/Forms/FilesTab.cs`: add the **Startup and scheduled tasks…** menu item calling `RunTool` with `StartupInventory.Run` and showing `StartupTasksWindow`
- [X] T046 [US2] Run T034, T036, T038, T040, T041 and fix T033–T045 until they pass; run the whole test project; manual check of quickstart scenarios 7, 8 and 12 with the scripted harness (render the window on `diag-startup` in `en`, `de`, `ja`; check clipping and the schedule words in each) and fix

**Checkpoint**: both tools work; the two P1 stories are done.

---

## Phase 5: User Story 3 - Keep the result (Priority: P2)

**Goal**: copy or save what the window shows, with no secrets.

**Independent Test**: the report of a fixture holds the same verdict/entries as the window, the date
and the notes, and no planted secret; saving never replaces silently.

- [X] T047 [US3] Create `src/HddBackup.Core/Diagnostics/ReportBuilder.cs` and the App side in `DiagnosticTexts.cs`: `string SshReportText(SshReport, Func<…> texts, CultureInfo, DateTime now)` and `string StartupReportText(StartupResult, …, bool includeInactive, IEnumerable<StartupEntry> visible)` — header (tool, source name, date in the culture's format), the state warning, findings per page or the visible rows as tab-separated lines, the "could not check" notes; only whitelisted properties are read (research R-14); strings `Report_Header`, `Report_Date`, `Report_Copy`, `Report_Save`, `Report_Saved`, `Report_Failed` added to all six resource files
- [X] T048 [P] [US3] Create `tests/HddBackup.Core.Tests/Diagnostics/ReportBuilderTests.cs`: the text of a known `SshReport` and `StartupResult` equals the expected text (stable order, header, date from an injected clock); the text for the six cultures contains the same facts; filtered rows only; **no planted secret in any report** (extends SC-004); a report built for a not-cleanly-unmounted source carries the warning
- [X] T049 [US3] Edit `src/HddBackup.App/Forms/SshWizardDialog.cs` and `src/HddBackup.App/Forms/StartupTasksWindow.cs`: **Copy** (`Clipboard.SetText`, tolerate a busy clipboard like `ExtractSummaryDialog`) and **Save as…** (`SaveFileDialog` with `OverwritePrompt = true`, UTF-8 with BOM so Windows 7 Notepad shows non-Latin text, default name `ssh-check_<source>.txt` / `startup-tasks_<source>.txt`, remembers `LastExtractFolder`-style folder in a new `LastReportFolder` setting in `src/HddBackup.App/Settings/PortableSettings.cs`), a message line `Report_Saved`/`Report_Failed`
- [X] T050 [US3] Run T048 and fix T047/T049 until it passes; manual check of quickstart scenario 6 (copy into Notepad, save twice to the same name) and fix. _Done: T048 passes; the clipboard text equals the report text in the scripted harness (en/de/ja). Not clicked by hand: the Save as… dialog and its overwrite prompt, and pasting into Notepad — listed in the manual hardware checklist (T057)._

---

## Phase 6: User Story 4 - Sources that are not what the tools expect (Priority: P3)

**Goal**: honest messages for non-Linux, not-cleanly-unmounted and unreadable sources.

**Independent Test**: a data-only ext4, the `dirty-journal` image and a source that fails to read give
the documented messages.

- [X] T051 [US4] Review and finish the degraded paths in `src/HddBackup.Core/Diagnostics/SshCheck.cs`, `StartupInventory.cs` and `DiagnosticsRunner.cs`: `NotALinuxRoot` results carry no findings and no entries; a not-cleanly-unmounted or damaged state is copied into the result so both windows show `Files_StateNeedsRepair`/`Files_StateDamaged`; unreadable key files, `shadow` and unit files give `Unknown`/`Unreadable` notes rather than exceptions; a source device error aborts with the cause (`Files_ReadError`) and shows no window
- [X] T052 [P] [US4] Create `tests/HddBackup.Core.Tests/Diagnostics/DegradedSourceTests.cs`: `browse-large` (data only) → both tools report not a Linux root; an empty ext4; `dirty-journal` (feature 006 fixture, whole-device image) → state warning present in both results and the tools still work on the on-disk content; a `ReadOnlyBlockDevice` that fails after N reads → `ImagingException` (`DeviceRemoved`) from the runner, source released; a damaged `shadow` (garbled lines) → `ShadowUnreadable`-style note and `Undetermined` only when it changes the answer; a missing `/etc/ssh/sshd_config` → defaults with source "default" and a note
- [X] T053 [US4] Edit `src/HddBackup.App/Forms/FilesTab.cs`: the Tools menu items stay enabled for any open file system (the tools explain); the **Tools** button tooltip states the reason when it is disabled (`Tools_NoFileSystem`, `Tools_Busy`); a read error while analysing shows `Files_ReadError` and no window; manual check of quickstart scenarios 9–11 and fix

---

## Phase 7: Polish & cross-cutting

- [X] T054 [P] Run the resource-parity test and a language sweep (quickstart scenario 12): render both windows in en, uk, de, fr, es, ja with the scripted harness, check for clipped text and that every schedule word reads naturally; take English screenshots of the wizard result page and the startup window into `docs/screenshots/` (`ssh-check.png`, `startup-tasks.png`); shorten labels or move text to tooltips where needed
- [X] T055 [P] Update `README.md` (English): a **Tools** section on the Files tab (what the two tools check, that they only read and advise, that secrets are never shown, that command lines are shown as written, that the FAT boot partition is not read so Raspberry Pi first-boot SSH is reported as "may be switched on", that network and firewall are not checked) with the two screenshots; add `specs/009-image-diagnostic-tools/` to the design documents list
- [X] T056 [P] Add an `## Unreleased` section to `docs/release-notes.md` for the Tools button, the SSH check and the startup and scheduled tasks list (check `tools/release/Get-ReleaseNotes.ps1` still passes its Pester tests)
- [X] T057 [P] Add scenarios 13–15 of `quickstart.md` as new rows to `docs/manual-hardware-checklist.md` (a real card as a Device source with before/after image hash, a 16–32 GiB image over USB 2.0 within 5 s, Windows 7 SP1 and Windows 11 runs of scenarios 1–8)
- [X] T058 Run `dotnet build HddBackup.sln -c Release` (0 warnings) and `dotnet test tests/HddBackup.Core.Tests -c Release`; run the Docker independent check (`check-diagnostics.sh`, `quickstart.md`) locally and confirm the `ext4-check` CI job passes on the branch (including the new `sshd -T` and `systemctl --root` oracles)
- [X] T059 Performance check against SC-003: measure both tools on `diag-startup`, on the Raspberry-Pi-like fixture and, if available, on a 16–32 GiB image over USB 2.0 (result within 5 s; count device reads); record results in `research.md` R-13 _Done on the fixtures (see research R-13). Not done: a 16-32 GiB image over USB 2.0 (manual checklist step 44)._
- [X] T060 Principle I review for the change (`docs/safety-review.md` format): add a section "Diagnostic tools (feature 009)" listing each rule (read-only source, no secrets in results/clipboard/files, no network, advice is text only, overwrite prompt on save) with its automated evidence (`DiagnosticsRunnerTests`, `SecretLeakTests`, `ReportBuilderTests`, read-only tests) and what is only manual
- [X] T061 Update `specs/009-image-diagnostic-tools/checklists/requirements.md` notes (clarification and amendment resolved) and set the spec **Status** to `Implemented` when everything above is done

---

## Dependencies & Execution Order

- **Gate**: T001 (layout approval) before every UI task. The constitution amendment is already applied.
- **Phase 1** (T001–T004) → **Phase 2** (T005–T016) → user stories. T002–T003 (fixtures) block the fixture
  tests; T004 only blocks the CI oracle.
- **Foundational internals**: T005 → T017 (Include) and T022 (`AllowUsers`); T007 → everything in
  `Diagnostics`; T008 → all `LinuxRootBuilder` tests; T011 → T014 and every fixture/runner test; T015
  (`SystemdScan`) → T022 and T039.
- **US1** (T017–T032) needs Phase 2. **US2** (T033–T046) needs Phase 2 only; it is independent of US1
  except that both edit `FilesTab.cs`, `DiagnosticTexts.cs` and `SecretLeakTests.cs` (do them one after
  the other).
- **US3** (T047–T050) needs the result types of US1 and US2 (its two halves can follow each story).
- **US4** (T051–T053) needs the tools to exist.
- **Polish** (T054–T061) after the stories it documents.

### Parallel opportunities

- Phase 2: T005 + T006, T008 + T009, T010 can run together with T007; T016 with T015's tests.
- US1: T018, T020, T023, T024, T025 together after T017/T019/T021/T022; T026 and T027 together.
- US2: T033/T034, T035/T036 and T037/T038 are three independent threads; T040 and T041 together.
- US3: T048 with T047's App half.
- Polish: T054–T057 are independent documents/checks.

```text
Example, US2 in parallel:  T033+T034 (schedule)  |  T035+T036 (cron)  |  T037+T038 (autostart)
then:                      T039 → T040 + T041 → T042 → T043 → T044 → T045 → T046
```

## Implementation Strategy

- **MVP = Phase 2 + US1** (the SSH check) and, as the second P1 slice, US2. Both are independent of
  each other after Phase 2, so either can ship first; US3 and US4 refine them.
- Keep Core free of text (codes and arguments only) so the SC-001/SC-002 matrices stay fast and
  language-independent; keep every secret out of every type (T020, T025, T041, T048 are the guards).
- Re-run the feature 008 test suites after T014: the Files tab layout and the extraction flow must not
  change.
