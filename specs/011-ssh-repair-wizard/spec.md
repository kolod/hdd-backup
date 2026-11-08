# Feature Specification: Repairing SSH Access from the SSH Check Wizard (Fixes and Public Keys)

**Feature Branch**: `011-ssh-repair-wizard`

**Created**: 2026-11-08

**Status**: Draft (layout approved 2026-11-08; implemented in code and tests; hand checks on a real image and card (checklist 49–52) pending)

**Input**: User description: "If editing is allowed, the SSH wizard can fix the problems it finds
and add signatures (public keys) to restore the SSH connection."

## Clarifications

### Session 2026-11-08

- Q1: What are the "signatures" that the wizard adds? → A (assumed, see Assumptions): public keys.
  The user supplies the public half of an SSH key pair (a line such as `ssh-ed25519 AAAA… name`
  or a `.pub` file) and the wizard adds it to the list of keys that may log in as an account
  (`authorized_keys`). A private key is never asked for, read or stored.
- Q2: When is the wizard allowed to change anything? → A: Only when editing is allowed for the
  open file system, exactly as in feature 010: **Edit** mode is on, and the image or card passed
  the checks of that feature. Otherwise the wizard stays read-only, as in feature 009, and says
  why repairs are not offered.
- Q3: Do repairs follow the rules of feature 010 for images and cards? → A: Yes. On an image the
  repairs wait in the pending-changes file with all other edits and take effect on **Save** (which
  also updates the checksum). On a card each repair is written at once, after a confirmation.
- Q4: Is setting or resetting a password part of this feature? → A: No. Only things that can be
  written as plain, visible text are repaired; a password hash is never created, shown or
  changed (feature 009, FR-010).

## Layout Mockup *(approved 2026-11-08)*

The **Result** page of the **SSH check** wizard (feature 009) keeps its verdict and reasons. In
edit mode each reason that can be repaired gets a **Fix…** button, and the page gets an **Add a
key…** button. Outside edit mode these controls are absent and a single line explains how to get
them (switch on **Edit** on the Files tab).

```text
┌─ SSH check — rootfs of raspberry-pi.img ─────────────────────────────────────────────────┐
│  Result  (4 of 4)                                                                         │
│                                                                                           │
│  ✖ SSH will not start by itself.                                                          │
│                                                                                           │
│  What to fix, in this order:                                                              │
│   1. ✖ The SSH server is installed but not set to start at boot.        [ Fix… ]          │
│   2. ⚠ pi: no key is authorized and password login is switched off.     [ Add a key… ]    │
│   3. ⚠ pi: the folder /home/pi/.ssh is open to other users (mode 755).  [ Fix… ]          │
│                                                                                           │
│  Waiting repairs (2):  Enable ssh.service · Add key SHA256:Qf3…9k to pi    [ Undo… ]      │
│  After these repairs the verdict will be:  ✔ SSH should work: pi with a key               │
│                                                                                           │
│  [ ◂ Back ]                                         [ Copy ] [ Save as… ]   [ Close ]     │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

**Fix…** and **Add a key…** open a small confirmation window that shows exactly what will change
before anything is queued or written:

```text
┌─ Fix: set ssh.service to start at boot ──────────────────────────────────────┐
│  This will create one link:                                                  │
│    /etc/systemd/system/multi-user.target.wants/ssh.service                   │
│      → /lib/systemd/system/ssh.service                                       │
│  Nothing else is changed.                                                    │
│                                                                              │
│                                                   [ Apply ]    [ Cancel ]    │
└──────────────────────────────────────────────────────────────────────────────┘

┌─ Add a key for pi ───────────────────────────────────────────────────────────┐
│  Public key (one line, or load a .pub file):                  [ Load… ]      │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │ ssh-ed25519 AAAAC3Nza…  alex@laptop                                    │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
│  Type: ssh-ed25519   Fingerprint: SHA256:Qf3…9k   Comment: alex@laptop       │
│  Added to: /home/pi/.ssh/authorized_keys (will be created, mode 600, owner pi)│
│                                                                              │
│                                                   [ Add ]      [ Cancel ]    │
└──────────────────────────────────────────────────────────────────────────────┘
```

On a card, **Apply** and **Add** are preceded by the card confirmation of feature 010 and the
change is written at once. On an image, they queue the repair as a pending change (shown in the
list "Waiting repairs" and in the tree of the Files tab) until **Save**.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Add my public key so I can log in again (Priority: P1)

A user has a card (or an image of one) whose Linux system has a working SSH server, but they
cannot log in: the password is unknown or password login is off, and no key of theirs is
authorized. They open the SSH check, see that the account has no matching key, press **Add a
key…**, paste the one-line public key from their computer, check the fingerprint, and confirm.
The key is added for the chosen account, with the right owner and permissions, and the wizard now
says "SSH should work: <user> with a key".

**Why this priority**: it is the most common reason for a lost SSH connection and the one repair
that cannot be done from the card's own system. It alone is a viable MVP.

**Independent Test**: on a fixture image with an enabled server and an account without keys, add
a valid key; the image shows the key in the account's authorized key list with correct owner and
permissions, the file system check is clean, and the wizard's verdict becomes "should work".

**Acceptance Scenarios**:

1. **Given** edit mode is on and an account with no `.ssh` folder, **When** the user adds a valid
   key, **Then** the folder (mode 700) and the key file (mode 600) are created with that
   account's owner and group, the file holds exactly the one key line, and the verdict updates.
2. **Given** an account that already has keys, **When** a key is added, **Then** the existing keys
   are kept byte for byte, the new key is appended on its own line, and a missing final line
   break in the old file does not join two keys.
3. **Given** the same key is already authorized for the account, **When** the user tries to add
   it, **Then** nothing is added and the user is told the key is already present.
4. **Given** a text that is not a public key (a private key, a truncated line, a certificate, a
   password, several lines with one invalid), **When** the user enters it, **Then** **Add** stays
   disabled and the reason is shown; a private key is refused with a warning and is not kept.
5. **Given** the authorized key file is a link, a directory, or lies on a path whose parts are
   links leaving the account's home, **When** the user adds a key, **Then** the wizard follows or
   refuses it by the same rules as the SSH server would (refusing when the target is outside the
   file system or cannot be written) and tells the user which.
6. **Given** the server is configured to read keys from another place (a key file setting other
   than the default, or a key lookup command), **When** the user adds a key, **Then** the key goes
   to the place the server actually reads, or, when that is a command that cannot be run here, the
   wizard says that it cannot add a key that would be used and offers nothing.

---

### User Story 2 - Fix the problems the wizard found (Priority: P1)

The wizard's reasons list what stops SSH from working. For each reason that can be fixed by a
small, well-defined change of text files or links, the wizard offers **Fix…**, shows what exactly
will change, and applies it after confirmation: switch the server on at boot (remove a mask,
create the enable link), allow what blocks every login (a setting that restricts logins to users
who do not exist, a login shell that forbids login), and so on, as listed in the requirements.

**Why this priority**: "SSH will not start" and "nobody can log in" are the other two common ways
to lose the connection, and knowing the cause without being able to remove it sends the user to
another tool.

**Independent Test**: on fixture images, one for each fixable reason, press **Fix…**; the verdict
changes to "should work" (or to the next remaining reason) and the file system check is clean.

**Acceptance Scenarios**:

1. **Given** the server is installed but disabled, masked or only started by an old init script,
   **When** the user confirms **Fix…**, **Then** the server is set to start at boot by the
   standard means of that system and the first page of the wizard shows it enabled.
2. **Given** a setting in the server configuration that blocks the account (login restricted to
   other users, a listed group the account is not in), **When** the user confirms **Fix…**,
   **Then** the offending line is switched off with a comment (not deleted), the wizard shows the
   effective setting as default or as the new value, and the rest of the file is unchanged.
3. **Given** the account's login shell is one that refuses login, **When** the user confirms
   **Fix…**, **Then** only the shell field of that one account line changes, to a login shell
   installed in the system, and every other field and line is unchanged.
4. **Given** the folder or key file of an account is open to other users, or is owned by someone
   else, **When** the user confirms **Fix…**, **Then** its mode and owner are corrected so that
   the server accepts the keys, and the content is not touched.
5. **Given** a reason that is a matter of a choice with a security cost (switch on password
   login, allow the administrator to log in), **When** the wizard shows it, **Then** it explains
   the cost, does not offer it as the first or as a default step, and requires the user to pick it
   explicitly; the key-based repair is always shown first.
6. **Given** a reason that cannot be repaired by this feature (the Raspberry Pi first-boot flag
   on the FAT partition, network or firewall, a password hash, an account that does not exist),
   **When** the wizard shows it, **Then** it has no **Fix…** button and gives the advice as text,
   as in feature 009.

---

### User Story 3 - See, undo and understand the repairs before they take effect (Priority: P2)

Because repairs on an image only wait for **Save**, the user can see what is waiting, undo single
repairs, and watch the verdict change as if the repairs were already made. A repair is shown in
words ("Enable ssh.service", "Add key SHA256:… to pi"), not as a list of file operations.

**Why this priority**: it makes the repairs safe to try and is what the user of an image expects
from feature 010; the repairs themselves work without it.

**Independent Test**: queue two repairs on an image, undo one, save; only the other is in the
saved image, and the original image file was unchanged until **Save**.

**Acceptance Scenarios**:

1. **Given** repairs are waiting, **When** the wizard runs or is reopened, **Then** it reads the
   file system as it will be after **Save** (the pending changes are laid over the real files), so
   its verdict, findings and account list already include the repairs, marked as "waiting".
2. **Given** a waiting repair, **When** the user undoes it, **Then** exactly the changes that
   belong to that repair leave the pending set and every other pending change stays.
3. **Given** the user later edits by hand a file a waiting repair changed, **When** the wizard
   runs again, **Then** it reads the hand-edited result, and undoing the repair is refused with an
   explanation when it would undo the hand edit.
4. **Given** the wizard is on a card, **When** a repair is applied, **Then** it is written at once
   and there is nothing to undo; the confirmation says so before the first byte is written.

---

### User Story 4 - The wizard stays read-only unless editing is allowed (Priority: P1)

Nothing changes for a user who only wants to look. Outside edit mode, for a file system that
cannot be edited (not cleanly unmounted, damaged, unsupported features, write-protected or
non-removable card), the wizard shows no repair controls, behaves exactly as in feature 009, and
a single line says that repairs need edit mode or why they are unavailable.

**Why this priority**: it is the safety condition for everything above (Constitution I).

**Independent Test**: open the wizard with edit mode off, on a non-editable fixture and on a
write-protected card double; no repair control exists and the device is not opened for writing.

**Acceptance Scenarios**:

1. **Given** edit mode is off, **When** the wizard shows its Result page, **Then** there are no
   **Fix…** or **Add a key…** controls and the line "Switch on Edit to repair" is shown.
2. **Given** the file system cannot be edited, **When** the wizard shows its Result page, **Then**
   the line gives the same reason as feature 010 for the **Edit** button being unavailable.
3. **Given** edit mode is switched off or left while the wizard is open, **When** the user looks
   at the wizard, **Then** its repair controls disappear or are disabled at once, and waiting
   repairs are treated as feature 010 treats any waiting change (kept, saved or discarded by the
   user's choice there).

---

### Edge Cases

- The home directory of the account does not exist, or lies on another partition (a separate
  `/home` partition is not part of the opened file system): the wizard says it cannot add a key
  there and does not create a home directory elsewhere.
- Several accounts can log in; the user adds the key for one of them chosen in the dialog, and
  `root` is offered only with the warning when the server does not allow root login.
- The key file has Windows line endings, no final line break, a byte-order mark, or very long
  lines: existing content is preserved byte for byte, only a line break is added where needed.
- A key option prefix (`from="…"`, `command="…"`, `no-pty`) typed by the user is accepted only as
  a valid, visible part of the line and is shown to the user before adding, with a warning that
  it restricts the key.
- A key type the installed server does not accept (for example an old DSA key): the wizard warns
  that the server will ignore it, but does not forbid adding it.
- The file system is full, has no free inode, or the device fails to write: the repair stops with
  the reason, and the rules of feature 010 apply (the file is entirely old or entirely new).
- The pending set already contains a change to the same file made by hand: the repair is built on
  that content, not on the original.
- A configuration file is split over drop-in files: a change goes to the file where the setting
  was found, or to a new drop-in file when the setting was only the default.
- Two fixes that touch the same file are queued one after another: the second builds on the first.
- The user cancels, closes the wizard, or the program is closed with waiting repairs: they behave
  as any pending change of feature 010 (kept until saved or discarded, with the same prompt).
- A private key or a password pasted into the key box: refused, cleared on request, never written
  to a log, to the pending-changes file or to a report.

## Requirements *(mandatory)*

### Functional Requirements

**Availability**

- **FR-001**: The SSH check wizard MUST offer repairs only when the open file system may be
  edited under feature 010 (Edit mode on; image, or a removable and writable card; the file system
  clean and with supported features). In every other case it MUST stay read-only and MUST show one
  line saying that repairs need edit mode, or why they are unavailable.
- **FR-002**: Every repair MUST be carried out through the editing mechanism of feature 010, so
  that all its safety rules apply unchanged: nothing is written before the change is checked to
  fit, only the edited partition is written, the order of writes survives an interruption, an
  image is changed only on **Save**, and a card is changed only after the confirmation of that
  feature.
- **FR-003**: The wizard MUST never write anything when it is merely opened or when the user
  moves between its pages.

**Fixes**

- **FR-004**: For each reason on the Result page that has a repair listed in FR-005, the wizard
  MUST show a **Fix…** button that opens a confirmation window naming every file, link, line or
  field that will change and what it will be changed to, and MUST change nothing until the user
  confirms.
- **FR-005**: The wizard MUST be able to repair these reasons, and no others:
  (a) the server is installed but not set to start at boot, or is masked: make it start at boot in
  the standard way of that system (a server that already starts by an old init script is not a
  problem and is not repaired);
  (b) a server setting excludes the account (user or group restriction, a disabled key login):
  make the narrowest change that lets the account in — add the account (or its group) to an allow
  list, remove it from a deny list, switch key login on;
  (c) the account's login shell refuses login: change the shell of that account to an installed
  login shell;
  (d) the account's `.ssh` folder or key file has a mode or owner that makes the server ignore
  the keys: correct the mode and the owner;
  (e) the server's host keys are missing and the server would fail to start: not offered (the
  keys are generated by the system at first start); the wizard only explains.
- **FR-006**: Settings that lower security (switching on password login, allowing the
  administrator to log in, allowing empty passwords) MUST NOT be offered as the first or only
  repair of a reason. When they are offered, the confirmation MUST state the risk in words and
  require an explicit extra choice; repairing by a key MUST always be listed before them. Allowing
  empty passwords MUST never be offered.
- **FR-007**: A change of a text file MUST keep every line it does not need to change byte for
  byte (line endings, comments, order). A line that is replaced MUST stay in the file as a
  comment with a note naming this program and the date, not be deleted, and the new line MUST be
  placed next to it in the same file; a setting that was only a default is added where the server
  will read it (never inside a conditional block). A file that does not end in a line break MUST get one only where a
  line is added.
- **FR-008**: Where a change of mode or owner is needed (FR-005 d, and for files and folders this
  feature creates), the editing mechanism of feature 010 MUST be extended to set the mode, owner
  and group of a created or existing entry, and MUST keep all other attributes (times other than
  the modification time of the touched entry, extended attributes, security labels) unchanged.

**Adding a key**

- **FR-009**: The wizard MUST offer **Add a key…** for any account that exists and has a home
  folder in the opened file system, including accounts that already have keys.
- **FR-010**: The user MUST be able to give the key by typing or pasting one line of text, or by
  loading a `.pub` file from this computer. The wizard MUST show the key's type, its fingerprint
  and its comment before adding, and MUST show the file it will be added to.
- **FR-011**: The wizard MUST accept only a well-formed public key of a type the standard SSH
  server knows (optionally with the standard option prefix), MUST refuse a private key, a
  certificate request, a password, an empty text and more than one key at a time, and MUST say why
  in plain words.
- **FR-012**: A key MUST be added to the key file the server actually reads for that account
  (honouring a non-default key-file setting, `~` and `%u`/`%h` patterns), creating the folder and
  the file with the correct mode and owner when they do not exist, and MUST be appended so that
  existing keys are preserved byte for byte. When the keys come from a command or the place cannot
  be written, the wizard MUST say so and offer nothing.
- **FR-013**: The wizard MUST detect that the same key is already authorized for the account
  (same type and key data, regardless of comment or options) and MUST not add it twice.
- **FR-014**: The wizard MUST NOT show, copy, save or log the contents of keys that were already
  in the system (feature 009, FR-010); it MAY show the fingerprint, type and comment of the key the
  user is adding. A private key given by mistake MUST NOT be kept, written or logged.

**Pending repairs**

- **FR-015**: Each repair MUST be kept as one named unit made of the changes it needs, shown in a
  list of waiting repairs in words, and the user MUST be able to undo a waiting repair on an
  image, which removes exactly its changes and no other pending change.
- **FR-016**: After a repair is queued or applied, the wizard MUST re-run its check against the
  file system as it will be (pending changes laid over the real files, feature 010) and MUST
  update the verdict and reasons, marking what depends on a waiting repair as "waiting for Save".
- **FR-017**: On a card, a repair MUST be written at once after the confirmation of feature 010 and
  the result (done or the reason it failed, with the offset on a write error) MUST be shown; on an
  image the wizard MUST NOT compute or touch a checksum itself (that is done by **Save**).
- **FR-018**: A repair that does not fit (no space, no free inode) or that the file system check
  of feature 010 rejects MUST be refused before any write, with the reason in plain words, and
  MUST leave the pending set and the file system as they were.

**Report and language**

- **FR-019**: The copied or saved text of the wizard MUST list the repairs that were queued or
  applied in this session, each in words, and MUST not contain key data (only fingerprint, type
  and comment of keys that were added).
- **FR-020**: All new texts MUST exist in every language the program offers, with the same
  behaviour in each, and MUST fit the windows without clipping.

### Key Entities

- **Repair**: one named, user-confirmed correction of one reason of the SSH check (enable the
  server, relax a restriction, change a login shell, correct folder permissions, add a key). It is
  made of one or more changes of feature 010 and is queued, undone, saved or discarded as a unit.
- **Public key**: the one-line public half of a key pair — type, key data, optional comment and
  options — with a fingerprint. Never a private key.
- **Account**: as in feature 009, an entry of the user database with a home folder and a login
  shell, and the key file the server reads for it.
- **Reason**: a finding of the SSH check that explains why login fails; may carry zero or one
  repair.
- **Waiting repair**: a repair queued on an image and not yet saved; read by the wizard as if
  already applied.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user who has a public key on their computer can go from "cannot log in" to "SSH
  should work" for an account with no authorized key in under 2 minutes, without leaving the
  program and without an ext4-capable system.
- **SC-002**: For every repairable reason in FR-005, on a prepared test image the verdict reaches
  "SSH should work" (or the next independent reason) after one repair, and an independent file
  system check of the saved image reports no errors.
- **SC-003**: In 100 % of tests where editing is not allowed (edit mode off, unclean, unsupported,
  write-protected, system disk), no repair control is shown and no write is attempted.
- **SC-004**: Every file the repairs do not need to change is identical, byte for byte, before and
  after; for every file that is changed, all lines other than the changed ones are identical.
- **SC-005**: An interruption at any write of a repair leaves the changed file entirely old or
  entirely new and the file system passing an independent check (with at most unreferenced blocks
  or inodes that the check removes).
- **SC-006**: In 100 % of tests, malformed key input, a private key and a duplicate key are
  refused before anything is queued, and nothing about a private key appears in the pending file,
  the saved image, the log or the report.
- **SC-007**: 9 of 10 first-time users, shown the Result page of a broken fixture, can name what
  each **Fix…** will change before pressing **Apply**.

## Assumptions

- "Signatures" in the request means public keys (the entries of `authorized_keys`). Adding other
  things (host keys, certificates, passwords) is out of scope.
- The target is the same kind of system as feature 009: systemd, OpenSSH server, the usual
  user database and home folders; other init systems or SSH servers are reported as "not
  recognised" and offer no repair.
- Repairs reuse feature 010 (pending set, preflight, card write rules, **Save**, checksum) and its
  ext4 writer; this feature adds only the setting of mode and owner (FR-008) and the repair logic
  on top. If that writer cannot yet create links or change attributes, adding it is part of this
  feature.
- The first-boot flag file of a single-board computer lives on the FAT boot partition, which is
  not read or written by either feature 009 or 010, so it is not repaired; the wizard keeps
  saying so (feature 009, FR-009).
- Network, firewall, and `Match` blocks stay unevaluated and unrepaired.
- The user knows which account they want to log in as; the wizard does not guess an account beyond
  pre-selecting the only account that can log in.
- A key typed by the user is trusted as an intent to allow that key; the wizard verifies format
  and shows the fingerprint, but cannot verify that the key belongs to the user.
