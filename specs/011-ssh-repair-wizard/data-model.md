# Data Model: SSH Repairs

## Writer additions (Core/Editing)

| Item | Fields / rules |
|------|----------------|
| `EditKind` + `CreateSymlink = 6`, `SetAttributes = 7` | existing values unchanged |
| `EditChange` (additions) | `LinkTarget` (string, CreateSymlink; 1..59 UTF-8 bytes, no NUL); `Mode` (ushort?, permission bits 0..0xFFF), `Uid` (uint?), `Gid` (uint?) for CreateFile / CreateDirectory / SetAttributes; `Group` (int, 0 = none); `Title` (`RepairTitle`) |
| `RepairTitle` | `Code` (`RepairKind`), `Arg1`, `Arg2` (short strings: unit, account, fingerprint; never key text) |
| Rules | `SetAttributes` on a missing path → `EditError.NotFound`; changing type bits is impossible; mode > 0xFFF refused. `CreateSymlink` in a missing parent → `NotFound`; existing name → `Exists` |
| `DiffFile` v2 | header version 2; per change: kind, path, newName, content, original stamp, **group, title code, args, linkTarget, mode?, uid?, gid?** (presence flags). v1 read as `Group = 0`. A v1-aware older program refuses v2 as "newer version" |
| `ImageEditSession` | `AddRepair(IReadOnlyList<EditChange>, RepairTitle)` → group id; `Undo(int group)` → `UndoResult {Done, Blocked(path)}`; `Groups` → list of `{Group, Title, ChangeCount}` |
| `CardEditSession` | `Apply(IReadOnlyList<EditChange>, progress, ct)` → `SaveResult`; the single-change overload calls it |

## Public key (Core/Repair/PublicKey.cs)

| Field | Meaning |
|-------|---------|
| `Type` | e.g. `ssh-ed25519` |
| `Body` | decoded key bytes (not shown) |
| `Options` | text before the type, or empty; shown with a warning when non-empty |
| `Comment` | rest of the line, may be empty |
| `Fingerprint` | `SHA256:` + unpadded base64 |
| `Warning` | `None`, `LegacyType`, `HasOptions` |
| `Line` | the normalised line to write: `options type base64 comment` as entered, trailing white space trimmed |
| `Refusal` (from `TryParse`) | `Empty`, `PrivateKey`, `Certificate`, `Several`, `UnknownType`, `BadEncoding`, `TypeMismatch` |

## Repair plan (Core/Repair/SshRepair.cs)

| Field | Meaning |
|-------|---------|
| `Kind` (`RepairKind`) | `EnableServer`, `UnmaskServer`, `AllowUser`, `UnblockUser`, `AllowGroup`, `EnableKeyAuth`, `RootKeysOnly`, `RootPassword` (risk), `EnablePasswordAuth` (risk), `FixShell`, `FixKeyFileModes`, `AddKey` |
| `Account` | account name when relevant |
| `Risk` | `None` / `LowersSecurity` — the dialog shows the risk text and requires an extra tick |
| `Summary` | list of `ChangeLine { Path, What (code), Before?, After? }` for the confirmation (never key data; for a key: fingerprint) |
| `Changes` | `IReadOnlyList<EditChange>` in safe order |
| `Alternatives` | other plans for the same reason (e.g. key-only root before password) |
| `Unavailable` | when not repairable: `RepairRefusal` code (`NoInstallSection`, `ListedInMatch`, `KeysFromCommand`, `NoHome`, `NoSuitableShell`, `OutsideFileSystem`, `Duplicate`, `ReadOnlyHere`) |

State: a plan is immutable; applying it creates a **Waiting repair** (image) or a **done** entry
(card). `Waiting repair = {Group, Title, ChangeCount}`; removed by Undo, Save or Discard.

## Check additions (Core/Diagnostics)

| Item | Rule |
|------|------|
| `SshdConfig.StrictModes` | `SettingValue<bool>`, default true |
| `Account.KeyFilePath` | path the server reads (expanded pattern; first existing, else first pattern); null with `AuthorizedKeysCommand` |
| `Account.KeyFileSafety` | `Ok`, `HomeWritable`, `SshDirUnsafe`, `FileUnsafe`, `WrongOwner`, `Unknown` + the offending path |
| `FindingCode.AccountKeyFileUnsafe` | status Problem when StrictModes on, Info when off; args: account, path, what |
| `LoginReason.KeyFileUnsafe` | key login unusable for that account (StrictModes on); ordered after `NoShell` in `ReasonOrder`... before `NoPasswordNoKey` as it explains why keys do not count |

## Session log (Core/Repair/RepairLog.cs)

`{ Kind, Account, Fingerprint?, Saved: bool (card) | Waiting: bool (image), Time }` — used by the
report text (FR-019). Contains no key data.

## Validation rules (from the spec)

- Add key: exactly one valid key, not a duplicate, account has a home inside the file system, key
  file location writable and inside the file system (links followed by `SystemReader` rules, a link
  leaving the file system → `OutsideFileSystem`).
- Risk repairs never in `Changes` of the primary plan; `Alternatives` only.
- A plan is rebuilt from the current (pending) tree each time it is shown or applied; applying a
  plan whose preconditions changed is refused with `Stale`.
