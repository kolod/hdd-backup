# Research: Repairing SSH Access from the SSH Check Wizard

All unknowns of the Technical Context are resolved here. IDs are referenced from plan.md.

## R-1 — What the 010 writer can and cannot express

**Finding**: `EditChange` has EditContent, CreateFile, CreateDirectory, Delete, Rename. New entries
get mode 0644/0755 and the *parent's* uid/gid (`NewInode`, `Ext4EditorStructure.cs`). There is no
symlink creation and no attribute change. Delete already frees fast symlinks
(`IsFastSymlinkOrSpecial`).

**Decision**: add two kinds:
- `CreateSymlink(path, target)`, fast symlinks only (target ≤ 59 bytes, stored in `i_block`, no data
  block, mode 0120777); a longer target is refused with `EditError.TooLong`. Every link this feature
  makes (`/etc/systemd/system/*.wants/ssh.service → /lib/systemd/system/ssh.service` and aliases) is
  shorter than 60 bytes.
- `SetAttributes(path, mode?, uid?, gid?)`, which rewrites only the inode fields given (permission
  bits only; type bits kept) and updates the inode checksum and ctime; mtime of the entry is not
  touched.
- `CreateFile` / `CreateDirectory` get optional mode/uid/gid, so a key file is created with 0600 and
  the account's owner in the *same* inode write (no window with the wrong mode).

**Rationale**: smallest additions that make every repair expressible; attributes in the creation
write remove a transient state from the write order.

**Alternatives**: a general `Chmod` for everything in the tree (rejected: no UI needs it); writing
links as regular files containing the path (rejected: not equivalent for systemd).

## R-2 — Repairs as groups in the diff file

**Decision**: each `EditChange` gets `Group` (int, 0 = none) and `Title` (a repair code plus up to
two short argument strings such as unit or account name, never key data). `ImageEditSession.AddRepair`
assigns a fresh group to a list of changes and adds them atomically (all or none, after dry-running
them on a `PendingTree`). `Undo(group)` removes exactly those changes if no later change depends on
them; if a later hand edit touched the same path the undo is refused (spec US3 scenario 3).
`DiffFile` format version 2 stores group, title and the new kinds; version-1 files are read as they
are. Unknown future kinds make the file "unreadable" (existing behaviour of 010).

**Rationale**: persisted groups survive closing the program; dependence check is a path comparison.

**Alternatives**: groups only in memory (rejected, see Complexity Tracking).

## R-3 — Write order of the new operations

**Decision**: follows 010's phases (Data → Allocation → Inode → Link). `CreateSymlink`: allocate
inode → write inode (target in `i_block`) → add directory entry → counters, i.e. no data phase. A
crash before the link phase leaves an unreferenced inode (what `e2fsck` frees). `SetAttributes` is a
single inode write (+ checksum): atomic at sector level when the inode lies in one 512-byte sector
(inode size 256, aligned), otherwise ordered as two writes of the same bytes into one sector
group; tested by the every-write interruption test. Within a repair, changes are ordered so every
prefix is harmless: create folder (mode 700) → create key file (mode 600) → (nothing else), or
write unit-link before alias link. An interrupted card repair therefore leaves either nothing, or a
harmless folder/empty-prefix state, never a half-written key line (file content is written whole by
copy-on-write, 010 R-5).

## R-4 — Reading the file system "as it will be"

**Finding**: `SystemReader(IFileTree)` works on any tree; `PendingTree` is an `IFileTree` and already
overlays edits, creations, deletions, renames. It does not yet carry link targets or attributes of
pending entries.

**Decision**: `PendingTree` nodes get `LinkTarget`, `Mode`, `Uid`, `Gid` overrides; `Make/Fill` use them,
so the unchanged `SshCheck` and the planner see symlinks and modes of waiting repairs. For a card the
host builds a fresh `BrowseSource` tree after each applied repair (the Files tab already has
`RebuildTree`). The wizard never keeps its own copy of the state.

## R-5 — Which reasons are repairable, and how

Decision per `LoginReason` / finding (everything else is explained as text only, FR-005):

| Reason | Repair | Notes |
|--------|--------|-------|
| `ServerNotEnabled` / `ServerMasked` | Masked: delete the `/etc/systemd/system/<unit>` symlink to `/dev/null` (or `/lib/systemd/system/<unit>` symlink to `/dev/null` — only the `/etc` one is removed; a masked unit by `/lib` is reported, not repaired). Not enabled: create the links `systemctl enable` would create, from the unit's `[Install]` section (`WantedBy=` → `<target>.wants/<unit>`; `Alias=` → alias link) | Reuses `SystemdScan` unit location. Unit without `[Install]`: no repair, text only |
| `ServerLegacy` (init script) | none | already starts at boot; spec FR-005(a) wording amended |
| `NotInAllowUsers` | append the account name to the `AllowUsers` line that excludes it | narrower than removing the restriction |
| `Denied` (DenyUsers / DenyGroups) | remove the account (or its group) from the list; comment the line out when it becomes empty | |
| group restrictions (`AllowGroups`) | append the account's primary group name (from `/etc/group`); no group name found → text only | |
| `KeyAuthOff` (`PubkeyAuthentication no`) | replace by `yes` in place | key-based, so always allowed |
| `RootNotAllowed` | offer `PermitRootLogin prohibit-password` (keys only) first; `yes` only as a second, explicitly confirmed, risk choice | FR-006 |
| `PasswordAuthOffNoKey` | primary: **Add a key…**; second, risk choice: `PasswordAuthentication yes` | FR-006 |
| `NoShell` | change the shell field of that account in `/etc/passwd` to the first of `/bin/bash`, `/usr/bin/bash`, `/bin/sh` that exists in the image and is listed in `/etc/shells` | none exists → text only |
| key file/folder modes or owner unsafe (new finding, R-6) | `SetAttributes` on home (remove group/other write), `.ssh` (700), key file (600), owner = account | content untouched |
| `EmptyPasswordRefused`, `PasswordLocked`, `MethodsNeedBoth`, `NoPasswordNoKey` | none / key repair | no password hashes (Clarification Q4); `NoPasswordNoKey` is solved by **Add a key…** |
| `BootFlagNotRead`, network, `Match` | none | outside the opened file system |

**Spec deltas** (applied to spec.md with this plan): FR-005(a) no longer mentions the init-script
case; FR-005(b) says "narrowest change: add the user to an allow list, remove them from a deny list,
switch a key-blocking setting on"; FR-007 says a setting is replaced in place, the old line kept as a
comment.

## R-6 — Key-file safety (`StrictModes`)

**Finding**: OpenSSH with `StrictModes yes` (default) ignores `authorized_keys` when the file, its
`.ssh` folder, or the home folder are writable by anyone but the account (or root), or are not owned by
the account (or root). Feature 009 does not check this; its verdict would say "should work" for an
account whose keys are ignored, and that is exactly the situation after a careless repair.

**Decision**: `AccountDatabase` records the path of the key file the server reads (first existing of
the `AuthorizedKeysFile` patterns, `%h %u %%` expanded as OpenSSH does) and the modes/owners of the
file, `.ssh`, and home from `FsEntry`. `SshdConfig` exposes `StrictModes`. `SshCheck` adds finding
`AccountKeyFileUnsafe` (status problem; counts as "key not usable" in the verdict when StrictModes is
on, note when off). Verdict wording for a "fixed" case does not change. Existing 009 tests keep
passing because their fixtures have owner-correct modes or `StrictModes no`; the ones that do not
are updated deliberately and listed in tasks.

## R-7 — Where a new or changed server setting goes

**Finding**: for most keywords `sshd` uses the **first** value it reads; `Include` is expanded where
it stands; settings after the first `Match` belong to that block.

**Decision** (`SshdEdit`): 1) the setting is set somewhere (`SettingValue.Source/Line`): edit *that*
line in place (comment out the old line with `# hddbackup <date>: ` and write the new line right
after it, or edit the token list in place for Allow/Deny lists); 2) it is only the default: add a
line in the main file immediately before the first `Match` line (or at the end when there is none);
the new line is never put after a `Match`. When the main file has an `Include` of a drop-in
directory that precedes the setting's position, the line goes into a new file
`/etc/ssh/sshd_config.d/00-hddbackup.conf` instead (first value wins). Line endings and the final
newline of the file are preserved; every other line is byte for byte unchanged. `Match` blocks are
never edited; a restriction that exists only inside a `Match` is reported, not repaired.

**Alternatives**: always a drop-in (rejected: some systems have no `Include`); rewriting the file
(rejected: violates FR-007).

## R-8 — Public key parsing and the key file

**Decision** (`PublicKey.Parse(string line)`): one line (trailing CR/LF trimmed; any other newline =
refused as "several keys"); optional options field (comma separated, quotes honoured, as in
`sshd(8)` AUTHORIZED_KEYS FILE FORMAT); key type from a fixed list (`ssh-ed25519`, `ssh-rsa`,
`ecdsa-sha2-nistp256/384/521`, `sk-ssh-ed25519@openssh.com`, `sk-ecdsa-sha2-nistp256@openssh.com`,
and the legacy `ssh-dss`, flagged "ignored by current servers"); base64 body must decode and its
first length-prefixed string must equal the type; comment is the rest. Refusals: empty, starts with
`-----BEGIN` or contains `PRIVATE KEY` (private key — text is not retained: the dialog clears the box
and keeps nothing), `ssh-rsa-cert-v01@openssh.com` and other certificate types, unknown type, bad
base64, type mismatch. Fingerprint = `SHA256:` + base64(SHA-256(body)) without padding.
Duplicate check compares type + body against the keys already in the file (read in memory only; never
shown). Append: if the file exists and does not end in `\n`, the line is prefixed with `\n` (CRLF files
keep their style); a BOM is kept.

## R-9 — Cards: one preflight per repair

**Finding**: 010 runs a full check per change (~12 s on 15 GiB). A repair has up to ~6 changes.

**Decision**: `CardEditSession.Apply(IReadOnlyList<EditChange>)` applies all changes on one
`Ext4Editor` (reads see its own writes), runs one `EditPreflight.Check` over the combined write set,
then replays it. If any change throws `EditException`, nothing is written. Same code path as 010's
single change (which becomes the list-of-one case).

## R-10 — Host interface between wizard and Files tab

**Decision**: `ISshRepairHost` (App): `CanRepair` + `WhyNot` (reason code), `Changed` event (edit mode
toggled, repair queued, undo, save, discard), `PendingTree`/`Tree` accessor to re-run the check,
`Apply(RepairPlan)`, `Undo(group)`, `Waiting` (title codes + args), `IsCard`. The wizard holds only the
host. Re-running `SshCheck` happens on a worker thread as in 009 and replaces the report; a stale
result (host changed again meanwhile) is dropped.

## R-11 — Fixtures and oracle

**Decision**: `make-editing-fixtures.sh` gets `edit-ssh-*` images built with `mke2fs -d` from a
generated tree (as root in Docker, so owners are real): `ssh-ok` (reference), `ssh-disabled`,
`ssh-masked`, `ssh-nokey` (+ password off), `ssh-allowusers`, `ssh-badshell`, `ssh-badmodes`,
`ssh-dropin` (Include + Match), `ssh-keyscommand`. `check-editing.sh` additionally compares
mode/owner/link target of changed entries with `debugfs stat` and runs `e2fsck -fn` on each saved
export. Real OpenSSH is *not* started in CI (no way to boot the image); `sshd -T -f` run in Docker over
the saved tree is used once in the manual quickstart to confirm the effective configuration.

## R-12 — Secrets

**Decision**: key lines the user enters are held in a `string` only while the dialog is open; the
planner puts them into `EditChange.Content` of the key file (necessarily, they are the data to write).
`Title` arguments never contain key text, only the account and the SHA-256 fingerprint. `RepairLog`
and reports hold the same. Diff files therefore contain a public key (the user's own input) but no
private key: private keys are refused before any object is created. `RepairSecretLeakTests` scans
log, report and title strings for the key body and for private-key markers.
