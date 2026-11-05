# Research: Diagnostic Tools for the Opened Linux File System

All unknowns of the Technical Context are resolved below. Code references are to the repository as
of 2026-11-05 (after feature 008). Semantics of OpenSSH, systemd and cron are those of Debian-family
systems such as Raspberry Pi OS (OpenSSH 9.x, systemd 252+, Debian cron).

## R-1: One reader that resolves symbolic links inside the image

**Decision**: `SystemReader` wraps an `IFileTree` (feature 008) and offers `Exists`, `Stat`,
`ReadText(path, maxBytes)`, `ReadLines`, `List(path)` and `ReadLink(path)`. Paths are absolute
paths of the image. Symbolic links are resolved by walking the components through
`IFileTree.GetChildren` and replacing a link with its target: an absolute target restarts at the
image root, a relative one continues in the link's directory, `..` pops a component; at most 40
links per resolution (loops fail with "too many links"). Directory listings and small file reads
are cached for the run. Text is decoded as UTF-8 (invalid bytes become U+FFFD); a file larger than
1 MiB is read up to the limit and reported as truncated.

**Rationale**: systemd enabling, `/etc/localtime`-style and merged-`/usr` layouts (`/lib` →
`usr/lib`, `/bin` → `usr/bin`) are all symbolic links whose targets are meaningful only inside the
image. `IFileTree` already serves entries with `LinkTarget`, sizes and modes, and serializes access,
so no new ext4 code is needed. Caching keeps the number of device reads small (SC-003).

**Alternatives**: `Ext4FileSystemView.GetEntry` only — walks components but does not follow links
and re-reads directories every time; extracting to a temporary folder and parsing there — writes
files (and possibly secrets) to the host, which Principle I and FR-010 forbid.

## R-2: The tools open their own read-only source

**Decision**: `DiagnosticsRunner` receives a description of the source (image path or `DeviceInfo`,
partition number, a display name) from the Files tab, opens its own `BrowseSource` /
`Ext4FileSystemView` on a worker thread (the same pattern as `ImagingEngine.RunExtract`), runs the
tool, and disposes everything. It is not an engine operation: there is no destination, progress
panel or taskbar progress. The UI shows an "Analysing…" state with **Cancel** for the second or two
it takes and then the result window.

**Rationale**: the tab's own view is serialized by one lock, so a tool that reads hundreds of files
would make the tree and the viewer stall. A second read handle on an image or device is harmless
(shared read) and device access stays read-only (`IDeviceAccess.OpenForRead`).

**Alternatives**: reuse the tab's view — stalls the tab; add `OperationKind.Diagnose` — drags in
the progress panel for a two-second job and a result type with nothing to show there.

## R-3: Recognising a Linux root, and when the tools are available

**Decision**: a file system is treated as a Linux system root when `/etc/passwd` exists and at
least one of `/etc/os-release`, `/usr/lib/os-release`, `/usr/bin` or `/bin` exists. The **Tools**
button is enabled whenever a file system is open (and no extraction runs); the check happens when a
tool starts. If the root is not recognised, the tool's first page says "no system configuration was
found on this partition" and offers only **Close** (User Story 4). The distribution is read from
`os-release` (`ID`, `PRETTY_NAME`) for wording, e.g. Raspberry Pi OS (`ID=raspbian`, or
`/etc/rpi-issue` present).

**Rationale**: disabling the button per partition would need the check on every selection change;
checking on start is cheap and gives a reason instead of a greyed-out control.

## R-4: Is the SSH server installed and set to start?

**Decision** (`SshCheck`, with `SystemdScan`):

- **Installed**: the server binary exists (`/usr/sbin/sshd`, found after link resolution) **or** a
  unit named `ssh.service` / `sshd.service` exists in the unit search path.
- **Unit lookup** order: `/etc/systemd/system`, `/run/systemd/system` (absent in an image),
  `/usr/lib/systemd/system`, `/lib/systemd/system`.
- **Masked**: the unit name in `/etc/systemd/system` is a link to `/dev/null`.
- **Enabled**: a link to the unit exists in a `*.wants` (or `*.requires`) directory under
  `/etc/systemd/system`, **and** the target that directory belongs to is reached from the default
  target (R-8); or an alias link (`sshd.service` → `ssh.service`) leads to an enabled unit.
- **Socket activation**: `ssh.socket` enabled in `sockets.target.wants` (with `ssh.service`
  not enabled) is reported as "started on demand".
- **Legacy**: if there is no systemd (`/lib/systemd/systemd` absent) but `/etc/rc?.d/S??ssh` or
  `/etc/init.d/ssh` links exist, they count as enabled for the SysV runlevels and the note says so.
- **First-boot switch**: if a unit such as `sshswitch.service` exists (Raspberry Pi OS), the verdict
  note says that a file on the FAT boot partition can switch SSH on at the first boot, which the
  tool cannot see (Q1, FR-009). The note is also given for Raspberry Pi OS whenever the server is
  installed but not enabled and the source has a FAT partition.

**Rationale**: these are exactly the facts `systemctl is-enabled` uses on disk; reachability from
the default target avoids reporting a service enabled in `graphical.target` on a system that boots
to `multi-user.target`.

**Alternatives**: "a link exists anywhere" — wrong for non-default targets; evaluating the whole
dependency graph — far more than needed (R-8 keeps it to targets).

## R-5: Effective SSH server settings

**Decision** (`SshdConfig`): parse `/etc/ssh/sshd_config` line by line: comments and blank lines are
skipped; keywords are case-insensitive; arguments are separated by blanks or `=`, quoted arguments
are honoured. `Include` is expanded in place (relative paths resolve against `/etc/ssh`; patterns
`*`, `?`, `[…]` through `WildcardMatcher`; files sorted by name; a depth limit of 16). **The first
value of a keyword wins**; multi-value keywords (`AllowUsers`, `DenyUsers`, `AllowGroups`,
`DenyGroups`, `ListenAddress`, `AuthorizedKeysFile`, `Port`) accumulate. Lines inside `Match …`
blocks (up to the next `Match` or end) are recorded as **conditional**: they are shown, never
applied to the unconditional view, and a conditional `PasswordAuthentication`/`PermitRootLogin`/
`AllowUsers` produces an "unknown for some users" note. Defaults (compiled into OpenSSH 9.x) apply
when a keyword is absent, and each setting records where it came from (file and line, or "default"):

| Keyword | Default | Used for |
|---------|---------|----------|
| `Port` | 22 | page 2 |
| `PermitRootLogin` | `prohibit-password` | root may log in with a key, not a password; `no`, `yes`, `forced-commands-only` |
| `PasswordAuthentication` | `yes` | password login |
| `KbdInteractiveAuthentication` (alias `ChallengeResponseAuthentication`) | `yes` | password login through PAM |
| `UsePAM` | `no` | password login through PAM; locked accounts (R-7) |
| `PubkeyAuthentication` | `yes` | key login |
| `PermitEmptyPasswords` | `no` | accounts with an empty password |
| `AuthorizedKeysFile` | `.ssh/authorized_keys .ssh/authorized_keys2` | where keys are looked up |
| `AuthorizedKeysCommand` | none | "keys may come from a command: cannot be checked" |
| `AuthenticationMethods` | any | combined methods (both needed) are reported |
| `AllowUsers` / `DenyUsers` / `AllowGroups` / `DenyGroups` | none | restrictions (R-7) |

Password login is possible when `PasswordAuthentication yes`, or when `KbdInteractiveAuthentication
yes` **and** `UsePAM yes`.

**Rationale**: these are the keywords that decide who can log in; semantics (first wins, Include in
place, Match ignored without criteria) are those of `sshd -T`, which is also the CI oracle (R-12).

**Alternatives**: a regular-expression pass over the file — breaks on Include, Match and quoting.

## R-6: Accounts, secrets, keys

**Decision** (`AccountDatabase`): read `/etc/passwd` (name, uid, gid, home, shell), `/etc/group`
(members for `AllowGroups`) and `/etc/shadow`. For the password field only a **state** survives the
parser, and the string is dropped immediately:

| Shadow field | State | Password login | Key login |
|--------------|-------|----------------|-----------|
| `$…`, other non-empty value not starting with `!` or `*` | `Set` | possible | possible |
| starts with `!` (including `!!`, `!$…`) | `Locked` | no | `UsePAM no` → refused ("account locked", OpenSSH rule); `UsePAM yes` → possible |
| `*` | `NoPassword` | no | possible |
| empty | `Empty` | only with `PermitEmptyPasswords yes` | possible |
| no entry / shadow unreadable | `Unknown` | unknown | possible |

A **login shell** counts as usable when it exists in `/etc/passwd` and is not one of
`/usr/sbin/nologin`, `/sbin/nologin`, `/bin/false`, `/usr/bin/false`. Authorized keys: the files
named by `AuthorizedKeysFile` (tokens `%h`, `%u`, `%%`; relative paths are inside the home) are read
and only **valid key lines are counted** (an optional options prefix, then a known key type such as
`ssh-ed25519`, `ssh-rsa`, `ecdsa-sha2-*`, `sk-*`, then a base64 blob); the lines themselves are
not kept. An account expiry field is reported as a note, not evaluated (the date of the image is
unknown). Accounts shown by default: `root` and those with uid ≥ 1000 other than `nobody`; system
accounts with a usable shell appear when the user asks for all.

**Rationale**: FR-010 requires secrets never to reach a result; reducing at parse time makes the
rule structural instead of a filter on the output. A guard test (R-12) checks it anyway.

## R-7: The SSH verdict

**Decision**: `SshCheck` computes, in this order, the verdict code with its reasons:

1. No server → `NoServer`.
2. Server present but not enabled (disabled, masked, or no start link):
   `NotEnabled`; with the first-boot note (R-4) it becomes `NotEnabledBootFlagPossible`
   (**not** "SSH is off", FR-009).
3. Enabled, but no account can log in → `NobodyCanLogIn`, listing for each account the first
   reason (shell, locked, no password and no key, root not allowed, not in `AllowUsers`, denied,
   password auth off and no key, key auth off and no password, empty password not allowed).
4. At least one account can → `Works` with the list "user — password / key" (a combined
   `AuthenticationMethods` is shown as "password and key").
5. Anything the tool cannot evaluate (conditional `Match` settings affecting the result,
   `AuthorizedKeysCommand`, unreadable `shadow`, unknown login restrictions with `%` patterns) →
   `Undetermined` **only when it changes the answer**; otherwise `Works` carries the note.

Account rules: `root` needs `PermitRootLogin` `yes` for password login, `yes` or `prohibit-password`
(or `without-password`) for key login, `forced-commands-only` only with a command key (reported as
"restricted"); `AllowUsers` patterns support `*`, `?`, `!` negation (`user@host` forms make the
account conditional); `AllowGroups`/`DenyGroups` use `/etc/group` and the primary group.

**Rationale**: the order follows what a user fixes first (server, then settings, then accounts), and
"Undetermined" is reserved for real uncertainty so that the wizard stays useful.

## R-8: What starts automatically (systemd and the rest)

**Decision** (`SystemdScan`, `AutostartScan`):

- **Targets**: `default.target` is resolved (link in `/etc/systemd/system`, else the distribution
  default). The set of **reached targets** is built by following, from the default target, the
  `Wants=`, `Requires=` and `Requisite=` of target units and the targets listed in their `.wants`/
  `.requires` directories (vendor and `/etc`). Only targets are walked.
- **Enabled units**: every link in a `*.wants`/`*.requires` directory under `/etc/systemd/system`
  whose directory's target is reached; kind = service, timer, socket, path, mount, by suffix.
  Missing link targets are reported inactive ("the unit file is missing"); links to `/dev/null` are
  masked.
- **What it runs**: `ExecStart=` of the unit (first line, `@`/`-`/`+`/`!` prefixes stripped); a timer
  shows `OnCalendar=`/`OnBootSec=` and the unit it activates (`Unit=` or the same-name service);
  `User=` (default root). Template units show the instance.
- **User units**: `~/.config/systemd/user/*.wants` of each account, listed as per-user services.
- **Boot scripts**: `/etc/rc.local` when executable and containing non-comment lines (one entry per
  command line); SysV `S??` links in `/etc/rc[2-5].d`/`/etc/rcS.d` (only when systemd is absent or
  the script has no unit, to avoid duplicates through the generator).
- **Desktop session**: `/etc/xdg/autostart/*.desktop` and `~/.config/autostart/*.desktop` (per
  account; the user entry overrides the system one by file name); `Hidden=true`,
  `X-GNOME-Autostart-enabled=false`, and an `OnlyShowIn` that excludes the session mark an entry
  inactive; `Exec=` field codes (`%f`, `%U` …) are stripped. Raspberry Pi OS autostart files
  (`/etc/xdg/lxsession/*/autostart`, `~/.config/lxsession/*/autostart`, `~/.config/labwc/autostart`):
  one entry per non-comment line, a leading `@` marks the program line of the LXDE format.

**Rationale**: reachability from the default target is the difference between "enabled" and "will
actually start"; the rest are the places a user can put something that runs on its own on such
systems.

**Alternatives**: listing vendor-preset units (`/usr/lib/systemd/system/*.wants`) — those are part of
the distribution's static dependencies and would bury the user's own entries; they are included only
for the units the SSH check needs.

## R-9: Cron tasks and schedule words

**Decision** (`CronScan`, `CronSchedule`):

- **Sources**: `/etc/crontab` and `/etc/cron.d/*` (system format: five fields, **user**, command);
  `/var/spool/cron/crontabs/<user>` (and `/var/spool/cron/<user>`) (user format: five fields,
  command; the file name is the user); `@reboot` and the other shortcuts (`@hourly`, `@daily`,
  `@midnight`, `@weekly`, `@monthly`, `@yearly`, `@annually`); environment lines (`NAME=value`)
  are skipped; `/etc/anacrontab` entries (period, delay, id, command) when present.
- **Periodic directories** `/etc/cron.hourly|daily|weekly|monthly`: a script is listed when it is an
  executable regular file (or a link to one) whose name matches `^[A-Za-z0-9_-]+$` (the `run-parts`
  rule); its schedule is taken from the `run-parts` line of `/etc/crontab` (or anacron) that invokes
  the directory, e.g. "every day at 06:25"; if no line invokes it, the script is listed inactive
  ("nothing runs this directory"). `/etc/cron.d` files whose names contain a dot are ignored by Debian
  cron and listed inactive with that reason.
- **Inactive**: commented-out lines are not listed at all (they are not entries); a crontab of a user
  that does not exist, malformed lines (shown with "could not be understood") and everything when the
  cron program (`/usr/sbin/cron`, `crond`) is absent are inactive with the reason. A cron service
  that is installed but not enabled only adds a note.
- **Schedule classes** (language-neutral, formatted by the App, R-10): `AtBoot`, `EveryMinute`,
  `EveryNMinutes(n)`, `HourlyAt(m)`, `DailyAt(times)`, `WeeklyOn(days, times)`, `MonthlyOn(day,
  times)`, `YearlyOn(month, day, time)`, `Custom(text)`; fields are parsed into sets (ranges, steps,
  lists, month and weekday names, `7` = Sunday); when both day-of-month and day-of-week are
  restricted cron runs on either, which is reported as `Custom`.

**Rationale**: this is the set of locations Debian-family cron reads, with the run-parts rules that
decide whether a periodic script really runs (FR-012, acceptance 2 and 4).

## R-10: Localization without losing structure

**Decision**: Core returns **codes and arguments** (`FindingCode`, `Verdict`, `StartupKind`,
`ScheduleClass`, plus strings that are data: paths, user names, commands). The App turns them into
text with the resource tables (`DiagnosticTexts`): one key per finding code, verdict and schedule
class (`Ssh_F_<Code>`, `Ssh_V_<Verdict>`, `Sched_<Class>`), with `{0}…` placeholders. Day and month
names come from `CultureInfo` (`DateTimeFormat`), times from the culture's short time pattern.
Reports are built by the App with the same functions, so the window, the clipboard and the saved
file read alike. A resource-parity test also requires a key for every enum value.

**Rationale**: keeps Core testable and language-free (Principle VI) and lets a language switch
re-render an open window.

## R-11: Windows and controls

**Decision**: the **Tools** button is a normal `Button` with a "▾" suffix that shows a
`ContextMenuStrip` below itself (no `SplitButton`, which .NET Framework 4.8 lacks). **SSH check** is
a `Form` with a header (step list "1 Server  2 Settings  3 Accounts  4 Result", the current one in
bold), a content area built per page (`Label`s with status glyphs ✔ ⚠ ✖ and a read-only `ListView`
for accounts), and **◂ Back / Next ▸ / Close**; the last page has **Copy** and **Save as…**.
**Startup and scheduled tasks** is a resizable `Form` with a `ComboBox` (kind filter), a `TextBox`
(search), a check box (only what is active, on by default; unchecked also shows inactive entries) and
a `ListView` in details mode (Kind, Name, What it runs / When, User, Source, Status). Both are modal
over the main window, reuse the icon and the dialog conventions of `ExtractSummaryDialog`, and fill
in on a worker thread (R-2) behind an "Analysing…" label.

## R-12: Test strategy and independent oracles

**Decision**:

- **In-memory matrix** (`LinuxRootBuilder` over `FakeFileTree`): ≥ 12 SSH configurations (SC-001) and
  a startup/cron reference system of ≥ 40 entries (SC-002), each with the expected verdict, reasons
  and entries written beside the test; fast, deterministic, easy to extend.
- **Real fixtures** (`make-diagnostic-fixtures.sh`, Docker): a few small ext4 roots built with
  `mke2fs -d` from staging trees: `diag-ssh-ok`, `diag-ssh-keys-only`, `diag-ssh-disabled`,
  `diag-startup`, plus a Raspberry-Pi-like one with a FAT boot partition. They prove the whole chain
  (ext4 reader → symlinks → parsers) on real symbolic links and modes.
- **Independent oracles in the `ext4-check` job**: effective SSH settings from `sshd -T` (the fixture's
  `/etc/ssh` is copied over the container's, host keys generated with `ssh-keygen -A`; `Match` blocks
  are not evaluated without `-C`, as in the tool) and enabled units from `systemctl --root=<root>
  list-unit-files --state=enabled`; the tests export the tool's results as listings and the script
  diffs them, as for feature 008.
- **Secrets**: the fixtures plant recognisable secrets (`$6$PLANTED…` hashes, `PLANTEDKEYBLOB` in key
  lines, an `-----BEGIN OPENSSH PRIVATE KEY-----` file in a home); `SecretLeakTests` renders every
  result and report in all six languages and asserts that no planted string occurs (SC-004).
- **Read-only**: the tools run on `ReadOnlyBlockDevice` (zero writes) and the image hash is compared
  before and after (SC-005).
- **UI**: the scripted harness used for feature 008 renders both windows in the six languages for the
  clipping check (SC-007).

## R-13: Limits and performance

**Decision**: per run, a file is read up to 1 MiB; a directory listing used for scanning is capped at
5 000 entries (more are reported as "list cut"); unit files are read only for enabled units, timers
and the units the SSH check names; `/usr/lib/systemd/system` is listed only to resolve names. Every
file and listing is cached by `SystemReader`. Expected: 200–800 small reads for a Raspberry Pi OS
root. Cancellation is honoured between files.

**Measured (2026-11-05, Windows 11, local SSD, `DiagnosticsPerformanceTests`)**: on a read-only device wrapper
the SSH check on `diag-startup` took 1 ms with 149 device reads (16 file reads, 22 listings); the startup list
took 37 ms with 319 device reads for 50 entries. On `diag-pi-like` (MBR, FAT boot partition) the SSH check took
40 ms with 69 device reads. `diag-ssh-ok`: 6 ms and 94 reads. The number of reads depends on the number of
files and directories read, not on the size of the card, so a 16–32 GiB card is expected to answer in about
as many reads; the hardware check over USB 2.0 (SC-003, manual checklist step 44) is still to be done.


## R-14: Report text and the secret guard

**Decision**: `ReportBuilder` composes the text from the structured result plus a text callback
(the App's `DiagnosticTexts`): header (tool, source name, state warning, date in the culture's format),
the findings or the entries, the "could not check" notes. It accepts only result types that hold no
free-text secret fields (accounts carry state enums and counts, entries carry paths and commands). A
command line is data from the image and could itself contain a secret (for example a password argument
in a cron line); it is shown as written — the user is looking at their own configuration — but the
README and the window say that command lines are shown verbatim. Saving never replaces an existing
file silently (`SaveFileDialog.OverwritePrompt`).
