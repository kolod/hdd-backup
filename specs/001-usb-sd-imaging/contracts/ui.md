# Contract: User Interface

## Main window (single window, two modes via tabs: Backup | Restore)

| Area | Content |
|------|---------|
| Device list | Model, capacity, bus (USB/SD), letters, table style; "external disk" tag; auto-refresh on device change |
| Scope | Radio: Whole device / Partition → partition grid (No., label, FS, size, offset) |
| Image | Path box + Browse; format `.img` / `.img.gz` (backup) or detected format (restore) |
| Options | Verify after operation, Eject when finished, Fast compression (gz backup) |
| Action | Start Backup / Write to Device / Verify Image (Restore tab, no device needed); Cancel during operation |
| Menu | Language (English, Українська, Deutsch, Français, Español, 日本語), About |

## Operation panel (shown during and after an operation)

```text
Phase: Writing… (2 of 4)                        [Cancel]
[████████████████████░░░░░░░░░░] 64.3 %
9.81 GB of 15.25 GB
Speed: 34.2 MB/s (avg 31.7 MB/s)
Elapsed: 00:05:16   Remaining: 00:02:51
```

- Refresh ≥ 1 Hz (250 ms timer). Remaining shows "calculating…" when unknown.
- Taskbar button mirrors progress (normal / error / paused-on-cancel-confirm states).
- On `Completed`: bar 100%, green banner "The device can now be safely removed." plus SHA-256
  (copy button) for backups; warning line if eject was vetoed (naming the process if known).
- Restore/Verify result lists each checksum: "Image file checksum: verified / mismatch / not
  available", "Content checksum: …"; Verify without sidecars offers "Save checksum files".
- On `Failed`/`Cancelled`: red/amber banner with localized cause, offset, and whether the device
  is safe to remove and whether content is complete.

## Confirm-write dialog (restore)

- Shows target model, capacity, bus, letters, partitions; image name and size; size warning if
  target larger; "external disk" checkbox when applicable.
- Default button is **Cancel**; Write button is enabled only after a 2 s delay.

## Window close during operation

- Backup: confirm → cancel and close. Restore: confirm with warning that target content will be
  incomplete → cancel, flush, release, then close.

## Localization contract

- All texts come from `Strings` resource keys; key set MUST be identical in all six resx files
  (enforced by test). Format strings use positional placeholders (`{0}`), never concatenation.
- Byte sizes use binary units with localized suffixes; numbers use the selected culture.
