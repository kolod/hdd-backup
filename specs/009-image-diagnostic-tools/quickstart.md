# Quickstart: Validating the Diagnostic Tools

## Fixtures (developer machine with Docker)

```bash
docker run --rm -v "$PWD/tests/fixtures/ext4:/w/scripts:ro" \
           -v "$PWD/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
           debian:bookworm bash /w/scripts/make-diagnostic-fixtures.sh
```

Produces small real ext4 roots (bare images, `.img.gz`): `diag-ssh-ok` (server enabled, password and
key login, a regular user), `diag-ssh-keys-only` (key only, key present for one user, locked second
user), `diag-ssh-disabled` (installed, not enabled), `diag-startup` (enabled services and timers, a
masked unit, `rc.local`, desktop and LXDE autostart, system and user crontabs, periodic scripts,
`@reboot`), and `diag-pi-like` (a Raspberry Pi OS-like root inside an MBR image with a FAT boot
partition, `sshswitch.service` present, SSH not enabled). They hold planted secrets (`$6$PLANTED…`,
`PLANTEDKEYBLOB`, a private key file) for the leak test.

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release --filter "FullyQualifiedName~Diagnostics"
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all tests pass: `SystemReaderTests` (links inside the image, loops, `..`,
absolute and relative targets), `SshdConfigTests` (first wins, Include, Match, defaults, quoting),
`AccountDatabaseTests` (every shadow state, keys counted not kept), `SshCheckMatrixTests` (≥ 12
configurations, SC-001), `SystemdScanTests`, `CronScanTests`, `CronScheduleTests` (words for every
class), `AutostartScanTests`, `StartupInventoryTests` (≥ 40 entries, SC-002), `FixtureTests` (the real
fixtures end to end), `SecretLeakTests` (SC-004), `ReadOnlyTests` (zero writes, image hash unchanged,
SC-005), resource parity (every code has a string in six languages).

## Independent check (Docker, also the `ext4-check` CI job)

```bash
bash tests/fixtures/ext4/check-diagnostics.sh <exports-dir> <fixtures-dir>
```

Expected: for each fixture the tool's effective SSH settings equal `sshd -T` of the fixture's
`/etc/ssh`, and the tool's enabled units equal `systemctl --root=<fixture> list-unit-files
--state=enabled` (research R-12).

## Manual scenarios

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Files tab, open `diag-ssh-ok.img`, **Tools ▾** | The menu lists the two tools; the button is enabled | FR-001 |
| 2 | **SSH check…** | Page 1 ✔ installed ✔ enabled; Next shows settings with their sources; accounts list; result "SSH should work: pi — a password and a key" | US1 |
| 3 | The same on `diag-ssh-keys-only` | Result says key login only; the user with a locked password and no key is listed with the reason | US1 |
| 4 | `diag-ssh-disabled` | Verdict "SSH will not start by itself …" naming the missing enable step | US1 |
| 5 | `diag-pi-like.img` | Verdict "not enabled … may be switched on at the first boot by a file on the boot partition (not checked)"; never "SSH is off" | FR-009 |
| 6 | Any result → **Copy**, paste into Notepad; **Save as…** twice to the same name | Text with source, date, findings, notes; no hash or key text; the second save asks before replacing | US3, FR-010 |
| 7 | **Startup and scheduled tasks…** on `diag-startup` | Entries per kind with schedules in words ("every day at 06:25", "at boot"); inactive ones hidden; unchecking "Only what is active" adds greyed ones with reasons | US2 |
| 8 | Filter Cron; search "backup" | Only matching rows; count line "{0} of {1} entries" | FR-014 |
| 9 | Open a data-only ext4 | Both tools say no system configuration was found | US4 |
| 10 | Open `dirty-journal.img` (feature 006 fixture) | Warning "not cleanly unmounted …" above the result | FR-004 |
| 11 | No file system open | **Tools** disabled with a tooltip | US4 |
| 12 | Switch the language to uk, de, fr, es, ja with a window open | Texts and schedule words change; no clipping | FR-017, SC-007 |
| 13 | Real card in the USB reader (Device source) | Same results as its image; the card's volumes stay mounted; image hash before/after equal | SC-005 |
| 14 | 16–32 GiB image over USB 2.0 | Result within 5 s | SC-003 |
| 15 | Windows 7 SP1 and Windows 11 | Scenarios 1–8 pass | FR-018; manual hardware checklist |

Scenarios 13–15 join `docs/manual-hardware-checklist.md`.
