# Quickstart: Validating SSH Repairs

## Fixtures (developer machine with Docker)

```bash
docker run --rm -v "$PWD/tests/fixtures/ext4:/w/scripts:ro" \
           -v "$PWD/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
           debian:bookworm bash /w/scripts/make-editing-fixtures.sh
```

Adds real ext4 images with a small Debian-like system: `edit-ssh-ok`, `-disabled`, `-masked`,
`-nokey`, `-allowusers`, `-badshell`, `-badmodes`, `-dropin` (Include + Match), `-keyscommand`.

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release --filter "FullyQualifiedName~Repair|FullyQualifiedName~Editing"
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all pass, among them:
- **PublicKeyTests** — good keys of every type, comment/options, refusals (private key, certificate,
  several lines, bad base64, type mismatch), fingerprint equals `ssh-keygen -lf` values stored in the test.
- **SshdEditTests / PasswdEditTests** — every other line byte-identical; in-place replace, token
  add/remove, insert before `Match`, drop-in rule, CRLF and missing final newline.
- **Symlink/attribute writer tests** — new kinds, `e2fsck`-clean, interruption at every write.
- **SshRepairPlannerTests** — each reason → plan (or refusal), risk plans only as alternatives.
- **SshRepairEndToEndTests** — for each broken fixture: plan → `ImageEditSession.AddRepair` → Save →
  re-run `SshCheck` on the saved image says "should work" → exported image passes `e2fsck -fn` and
  the changed entries have the expected mode/owner/link target (`check-editing.sh`); original image
  unchanged until Save (SHA-256 equal); Undo leaves exactly the other repairs.
- **Card tests** — one preflight per repair, writes only inside the partition, fault injection.
- **RepairSecretLeakTests** — no key body or private-key marker in log, titles, report.
- **009 regression** — `SshCheckMatrixTests`, `SshFixtureTests` unchanged except the deliberately
  updated key-file-safety cases.

## Independent oracle (once, by hand or CI `ext4-check` job)

```bash
# on a saved fixture export
e2fsck -fn edit-ssh-nokey.saved.img
debugfs -R "stat /home/pi/.ssh/authorized_keys" edit-ssh-nokey.saved.img   # mode 0600, uid/gid of pi
debugfs -R "rdump /etc /tmp/etc" edit-ssh-nokey.saved.img && sshd -T -f /tmp/etc/ssh/sshd_config | grep -i -e pubkey -e allowusers
```

## Manual checks (checklist rows to add after row 48)

1. Open a real Raspberry Pi OS image, **Edit**, **Tools → SSH check…**: a clean system shows no
   repair buttons except **Add a key…**; add your key, **Save**, flash/restore to a card, boot, log in
   with the key.
2. Same on a physical card in edit mode: add a key, boot, log in.
3. Break the image on purpose (disable `ssh`, set `AllowUsers other`), run **Fix…** for each, boot.
4. Windows 7 and 11: both dialogs, the `.pub` file picker, all six languages (clipping).
