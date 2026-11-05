# Feature Specification: Diagnostic Tools for the Opened Linux File System (SSH Check, Startup and Scheduled Tasks)

**Feature Branch**: `009-image-diagnostic-tools`

**Created**: 2026-11-05

**Status**: Implemented (layout approved 2026-11-05; real-device, Windows 7 and timing checks pending, see the manual hardware checklist steps 43-45)

**Input**: User description: "Next to the Extract button, a Tools button with a drop-down list of
tools: (1) a wizard that checks whether the SSH server is set up to start and whether remote login
to the system is set up; (2) a tool that shows what starts automatically and the tasks in cron."

## Clarifications

### Session 2026-11-05

- Q1: Should the SSH check also read the FAT boot partition, where Raspberry Pi OS keeps the flag
  file that switches SSH on at the first boot? → A: No. Only the ext4 file system is read. When
  the result could depend on something on another partition, the tool says honestly that it could
  not check that.

## Layout Mockup *(approved)*

The **Files** tab of feature 008 gets a **Tools** button to the left of **Extract…**. It opens a
small menu; each item opens a window about the file system that is open on the tab. Nothing is
changed in the image or the device.

```text
│ ├──────────────────────────────────┴────────────────────────────────────────────────────┤│
│ │ 1,567 files, 13 directories                              [ Tools ▾ ]  [ Extract… ]    ││
│ │                                                          ┌──────────────────────────┐ ││
│ │                                                          │ SSH check…               │ ││
│ │                                                          │ Startup and scheduled    │ ││
│ │                                                          │ tasks…                   │ ││
│ │                                                          └──────────────────────────┘ ││
```

**SSH check** (a wizard, four pages; **Next ▸**, **◂ Back**, **Close**; the last page can be saved):

```text
┌─ SSH check — rootfs of raspberry-pi.img ─────────────────────────────────────────────────┐
│ 1 Server   2 Settings   3 Accounts   4 Result                                             │
│ ──────────────────────────────────────────────────────────────────────────────────────── │
│  ✔ The SSH server is installed (OpenSSH server).                                          │
│  ✔ It is set to start at boot (ssh.service is enabled).                                   │
│  ⚠ Raspberry Pi OS can also switch SSH on at the first boot with a file on the FAT       │
│    boot partition. This tool reads only the Linux file system and did not check that.     │
│                                                    [ ◂ Back ] [ Next ▸ ] [ Close ]        │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

**Startup and scheduled tasks** (one window, a list with a filter):

```text
┌─ Startup and scheduled tasks — rootfs of raspberry-pi.img ───────────────────────────────┐
│ Show: [ All ▾ ]  Search: [ ______________ ]  [ ] Only what is active                     │
│ ┌──────────┬───────────────────────┬──────────────────────────┬───────┬─────────────────┐│
│ │ Kind     │ Name                  │ What it runs / When      │ User  │ Source          ││
│ │ Service  │ ssh.service           │ /usr/sbin/sshd -D        │ root  │ multi-user.target││
│ │ Cron     │ logrotate             │ every day at 06:25       │ root  │ /etc/crontab    ││
│ │ Cron     │ backup.sh             │ every 15 minutes         │ pi    │ crontab of pi   ││
│ │ Desktop  │ vnc-server            │ /usr/bin/vncserver       │ pi    │ ~/.config/autos…││
│ └──────────┴───────────────────────┴──────────────────────────┴───────┴─────────────────┘│
│ 41 entries (3 inactive)                                  [ Copy ] [ Save as… ]  [ Close ] │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Find out whether I can log in to this card over SSH (Priority: P1)

A user has a backup of a single-board computer (or the card itself) and wants to know whether,
once it boots, they can reach it with SSH, and as which user. They open **Tools → SSH check…**.
The wizard walks through the three things that decide it — is the server set up to start, what do
its settings allow, which accounts can log in — and ends with a plain verdict and the reasons.

**Why this priority**: "Why can't I connect to my Pi?" is the most common question about such
cards, and the answer is in files the user can already browse but would have to interpret.

**Independent Test**: open fixtures prepared with known configurations (server enabled or not,
password or key only, root login on or off, locked and unlocked accounts) and compare the verdict
and the reasons with the expected ones.

**Acceptance Scenarios**:

1. **Given** a file system with the SSH server installed and enabled at boot, **When** the user
   opens the wizard, **Then** the first page says so, and names what was found (the service and
   where it is enabled).
2. **Given** a file system where the server is installed but not enabled (or masked), **When** the
   wizard reaches the result, **Then** the verdict is that SSH will not start by itself and the
   reason names the missing step.
3. **Given** a server whose settings allow password login and an account with a password set,
   **When** the wizard reaches the result, **Then** it lists that account as able to log in with a
   password.
4. **Given** a server that allows only key login and an account without any authorized key,
   **When** the wizard reaches the result, **Then** it says that nobody can log in and why.
5. **Given** settings that do not allow root login, **When** the result is shown, **Then** root is
   not listed as able to log in, and the reason says it is not allowed.
6. **Given** accounts that are locked, have no login shell, or have an empty password, **When**
   the accounts page is shown, **Then** each such account carries the matching explanation.
7. **Given** no SSH server is installed, **When** the wizard starts, **Then** the verdict is
   "no SSH server" on the first page and the later pages explain that they do not apply.
8. **Given** a result that could depend on a file on another partition (for example the boot
   partition of a Raspberry Pi card), **When** the result is shown, **Then** it says that this
   could not be checked, instead of claiming that SSH is off.

---

### User Story 2 - See what starts automatically and what is scheduled (Priority: P1)

The user wants to know what runs on this system without being asked: services that start at boot,
boot scripts, programs started with a desktop session, and everything in cron. They open
**Tools → Startup and scheduled tasks…** and get one list they can filter and search.

**Why this priority**: it answers "what does this card do on its own?" (for example before
cloning it, or when something keeps changing files) without reading dozens of files by hand.

**Independent Test**: open fixtures with known sets of enabled services, boot scripts, autostart
entries and crontabs; check that the list contains exactly the expected entries with the expected
schedules and commands.

**Acceptance Scenarios**:

1. **Given** services enabled at boot, **When** the window opens, **Then** each is listed with its
   name, the command it runs, the user it runs as (when known) and where its enabling comes from.
2. **Given** a system crontab, drop-in cron files, per-user crontabs and periodic directories
   (hourly, daily, weekly, monthly), **When** the window opens, **Then** every active entry is
   listed with its schedule in words ("every day at 06:25"), the original schedule text, the
   command and the user.
3. **Given** an entry that starts at boot through cron, **When** the list is shown, **Then** it is
   listed as such ("at boot").
4. **Given** disabled or masked services, commented-out cron lines, or scripts that cron would
   skip, **When** the list is shown, **Then** they are not mixed in with the active ones; they
   appear only when the user asks to see inactive entries, marked as inactive with the reason.
5. **Given** the filter "Cron" or a search text, **When** the user applies it, **Then** only the
   matching entries stay, and the count line says how many of how many.
6. **Given** an entry whose file cannot be read, **When** the list is shown, **Then** the entry is
   marked with the reason and the rest of the list is unaffected.

---

### User Story 3 - Keep the result (Priority: P2)

The user wants to attach the findings to a ticket or compare two cards. They copy the result to the
clipboard or save it as a text file.

**Why this priority**: useful, but the tools are valuable without it.

**Independent Test**: produce the report for a fixture, save it and check that it holds the same
verdict and entries as the window and none of the secret values.

**Acceptance Scenarios**:

1. **Given** the last page of the wizard or the list window, **When** the user chooses **Copy** or
   **Save as…**, **Then** the text holds the source name, the date, the findings or entries and
   the "could not check" notes, in the language of the interface.
2. **Given** a file name that already exists, **When** the user saves, **Then** it is not replaced
   without asking.

---

### User Story 4 - Sources that are not what the tools expect (Priority: P3)

The open file system is not a Linux root (a data partition), is damaged, or was not unmounted
cleanly. The tools say what they can and cannot tell, and never present guesses as facts.

**Why this priority**: affects few users but protects everyone from wrong conclusions.

**Independent Test**: open a data-only ext4, a damaged image and a "not cleanly unmounted" image
and check the messages.

**Acceptance Scenarios**:

1. **Given** an ext4 partition that is not a Linux root (no system configuration), **When** the
   user opens the **Tools** menu, **Then** the items are available but each says at its start
   that no system configuration was found there, or they are disabled with that reason.
2. **Given** a file system that was not cleanly unmounted, **When** a tool runs, **Then** its
   result carries the warning that recent changes may be missing (the journal is not replayed).
3. **Given** no file system is open (or the source cannot be read), **When** the user looks at the
   **Tools** button, **Then** it is disabled.

---

### Edge Cases

- SSH settings spread over several files (a main file that includes a drop-in directory), with
  the first occurrence of a setting winning; settings that appear only inside conditional blocks.
- A setting absent from every file: the documented default of the server applies and the page
  says that the value is the default.
- Several SSH server packages or a server started on demand (socket activation) instead of at boot.
- A user whose home directory or key file is missing, empty or unreadable; key files with
  comments, options, blank lines and several keys.
- Accounts with a password that has expired or is locked by a prefix, with a disabled shell, or
  with a shell that is not a login shell.
- Services enabled through links that point to missing units; masked units; units enabled for
  targets other than the default one; template units with instances.
- Cron entries with environment lines, `@reboot`, `@daily` and similar shortcuts, ranges, steps,
  lists, day-of-week names, and lines that are malformed.
- Periodic cron directories whose scripts are not executable or have names cron ignores.
- A crontab for a user that no longer exists.
- Very large systems: thousands of enabled units or very long crontab files.
- Non-ASCII user names and commands; very long command lines.
- A file system with no system configuration at all (a data partition, an empty image).

## Requirements *(mandatory)*

### Functional Requirements

**Tools menu**

- **FR-001**: The Files tab MUST show a **Tools** button next to **Extract…** that opens a menu with
  two items: **SSH check…** and **Startup and scheduled tasks…**.
- **FR-002**: The button MUST be disabled while no file system is open or while an extraction is
  running, and each item MUST work on the file system open on the tab (image or device).
- **FR-003**: Both tools MUST be read-only: nothing is written to the image or the device, and no
  file in them is changed; neither tool offers to change a setting (advice is text only).
- **FR-004**: Both tools MUST state at the top which file system they examined and MUST show the
  warning of feature 008 when it was not cleanly unmounted.

**SSH check**

- **FR-005**: The wizard MUST report whether an SSH server is installed and whether it is set to
  start at boot (including the cases "disabled", "masked" and "started on demand"), naming the
  files or links the conclusion rests on.
- **FR-006**: The wizard MUST report the server settings that decide who can log in and how: the
  port, whether the administrator account may log in, whether password login and key login are
  allowed, and any restriction to certain users or groups; for each it MUST say whether the value
  is set explicitly (and where) or is the default.
- **FR-007**: The wizard MUST list the accounts that exist and, for each, whether it could log in
  with a password and with a key (password set, locked or empty; login shell; authorized keys
  present and how many), with an explanation for every "no".
- **FR-008**: The wizard MUST end with a verdict in plain words — "SSH should work: <user> with a
  password / with a key", "SSH will not start", "the server allows nobody to log in", or "cannot
  be determined" — together with the reasons, in the order a user would fix them.
- **FR-009**: When the outcome could depend on something this tool does not read (the boot
  partition of a single-board computer, network or firewall configuration, settings applied at the
  first boot), the wizard MUST say so, and MUST NOT report "SSH is off" on that basis alone.
- **FR-010**: Password hashes, private keys and the contents of authorized key entries MUST never
  be shown, copied or saved; only facts about them (present, absent, locked, how many) are shown.

**Startup and scheduled tasks**

- **FR-011**: The tool MUST list, in one list, everything that starts automatically: services and
  timers enabled for start-up, boot scripts, and programs started with a desktop session (system
  wide and per user), each with its kind, name, what it runs, the user (when known) and its source.
- **FR-012**: The tool MUST list every cron task: the system crontab, drop-in cron files,
  per-user crontabs, boot-time entries (`@reboot`) and the scripts of the periodic directories that
  would actually run, each with its schedule in words, the original schedule text, the command,
  the user and its source file.
- **FR-013**: Inactive entries (disabled or masked units, commented-out lines, scripts that would
  be skipped) MUST be kept out of the main list and MUST be shown only on request, marked
  inactive with the reason.
- **FR-014**: The list MUST be filterable by kind and searchable by text, MUST show how many entries
  are shown out of how many, and MUST keep working when single entries cannot be read, which are
  then marked with the reason.

**Result**

- **FR-015**: The user MUST be able to copy the result as text and to save it to a file on this
  computer; the text holds the examined source, the date, the findings or entries and the notes
  about what could not be checked, and never a secret (FR-010). An existing file MUST NOT be
  replaced without asking.

**Input that is not as expected**

- **FR-016**: When the file system holds no system configuration, or a file the tool needs is
  unreadable, the tool MUST say what is missing and MUST NOT present a guess as a fact.

**Platform**

- **FR-017**: All new text (labels, page texts, explanations, verdicts, tooltips) MUST come from
  the resource tables in all six supported languages, and the windows MUST not clip text in any of
  them.
- **FR-018**: The tools MUST work on Windows 7 SP1 through Windows 11, in the single executable.

### Key Entities

- **Source**: the file system open on the Files tab (image or device, partition), with its state
  (clean, not cleanly unmounted, damaged).
- **Finding**: one fact of the SSH check — a statement, a status (ok, problem, unknown), the files
  it rests on and, for a problem, what to do about it.
- **Account**: a user of the examined system with the facts that matter for login (password state,
  login shell, authorized key count); never the secrets themselves.
- **Verdict**: the plain-words outcome of the SSH check, with its reasons and its "could not check"
  notes.
- **Startup entry**: something that starts automatically — kind, name, command, user, source,
  active or inactive and why.
- **Scheduled task**: a cron entry — schedule in words and as written, command, user, source,
  active or inactive and why.
- **Report**: the text form of a verdict or of the list.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a prepared set of at least 12 configurations (server enabled, disabled, masked,
  absent; password only, key only, both; administrator login allowed or not; locked, empty-password
  and no-shell accounts; settings spread over included files), the verdict and the reasons equal
  the expected ones in 100% of the cases.
- **SC-002**: The list of a prepared system equals the expected set exactly: every active startup
  entry and every cron task is present once, with the right schedule words and user, and no inactive
  entry appears in the main list (100% of a reference set of at least 40 entries).
- **SC-003**: Either tool shows its result within 5 seconds for an image or device of up to 32 GiB,
  regardless of the size of the file system.
- **SC-004**: No password hash, private key or authorized-key text appears in any window, copy or
  saved file: a scan for the secrets planted in the test file systems finds none.
- **SC-005**: After each tool, the SHA-256 of the image is unchanged and tests with a fake device
  observe zero write requests.
- **SC-006**: A first-time user can answer "can I reach this card over SSH, and as whom?" in under
  one minute using the wizard, without reading any configuration file.
- **SC-007**: In every one of the six languages, both windows show no clipped text at 100% display
  scaling on Windows 11.

## Assumptions

- **Scope amendment**: Principle VII of the constitution (as amended in 1.3.0) covered browsing and
  extracting files. Interpreting system configuration is a new kind of read-only use of the same
  file system. The MINOR amendment 1.4.0 that adds read-only diagnostics of the opened file
  system to Principle VII was applied on 2026-11-05, before planning.
- Depends on feature 008 (the Files tab, the read-only file system view, the preview and
  extraction) and on its safety rules.
- Only the ext4 file system that is open on the tab is read. The FAT boot partition of a
  Raspberry Pi card is not read (Q1); the tools say so where it matters.
- The examined system is a Linux system of the usual kind (systemd, OpenSSH server, Vixie-style
  cron as in Raspberry Pi OS and Debian). Older init scripts and desktop autostart entries are
  listed too; other init systems and other SSH servers are reported as "not recognised".
- The effective SSH settings are worked out from the files by the program itself; the server is
  never run. Settings inside conditional blocks are shown as conditional rather than evaluated for
  a particular user or address.
- "Remote login" means login over SSH. Other ways in (VNC, telnet, serial console, web
  interfaces) and the network side (addresses, firewall rules, router) are out of scope; the
  wizard says it does not check them.
- The tools report and advise; they never change the image. Changing a setting means editing the
  file in the system or writing a changed copy, which is outside this feature.
- Timers of the init system are listed with the startup entries (they start things on a
  schedule); "cron" in the user's wording means the cron tasks of FR-012.
- Passwords are never tested or guessed; only the stored state (set, locked, empty) is read.
- The date format and the schedule words follow the interface language.
