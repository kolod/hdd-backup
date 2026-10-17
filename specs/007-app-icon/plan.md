# Implementation Plan: Application Icon

**Branch**: `007-app-icon` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/007-app-icon/spec.md`

## Summary

Give HddBackup its own icon: the Papirus `media-flash` SD card (GPL-3.0) with a blue body. The
hand-drawn Papirus sizes are rendered and packed into a multi-size `app.ico` by a Docker script
kept with the artwork; the icon is the executable's Win32 icon and is used by the main window and
every dialog; the About window shows it large with a credit line.

## Technical Context

**Language/Version**: C# 7.3, .NET Framework 4.8

**Primary Dependencies**: none new (icon built offline with `librsvg2-bin` and `icoutils` in
Docker; the build only consumes the committed `app.ico`)

**Storage**: N/A

**Testing**: xUnit check of the `.ico` entries; manual Explorer/taskbar/DPI scenarios

**Target Platform**: Windows 7 SP1 – Windows 11

**Project Type**: desktop app (single WinForms executable)

**Performance Goals**: N/A

**Constraints**: executable grows ≤ 200 KB (SC-003); readable at 16 px; visible on dark and light
backgrounds

**Scale/Scope**: 1 icon file, 6 SVG sources, 1 helper class, 5 windows, 1 string × 6 languages

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Pre-design | Post-design |
|---|-----------|------------|-------------|
| I | Data Safety First | ✅ No effect on imaging | ✅ |
| II | Faithful Imaging | ✅ N/A | ✅ |
| III | Portable & Compact | ✅ Icon embedded in the single executable | ✅ `app.ico` ≈ tens of KB, embedded twice (Win32 + managed) well under 200 KB; license-compatible artwork (GPL-3.0 = program license), credited |
| IV | Win 7 – 11 | ✅ ICO format | ✅ BMP entries ≤ 64 px, PNG 256 px entry (Vista+) |
| V | Localized UI | ✅ One new string in six languages | ✅ |
| VI | Test-Verified Disk I/O | ✅ N/A | ✅ Icon entries checked by a unit test |
| VII | Simplicity | ✅ Ready-made artwork, no new runtime code paths beyond setting icons | ✅ |

No violations.

## Project Structure

### Documentation (this feature)

```text
specs/007-app-icon/
├── plan.md, research.md, data-model.md, quickstart.md, preview.png
├── contracts/ui.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
assets/icon/
├── papirus/media-flash-<N>x<N>.svg   # upstream, unchanged
├── media-flash-<N>x<N>.svg           # recolored
├── make-icon.sh                      # Docker: render + icotool → src/HddBackup.App/app.ico
└── README.md                         # origin, license, change, rebuild

src/HddBackup.App/
├── app.ico                           # committed build output
├── HddBackup.App.csproj              # ApplicationIcon + EmbeddedResource
├── Forms/AppIcon.cs                  # NEW: shared multi-size icon
├── Forms/MainForm.cs, AboutDialog.cs, ConfirmWriteDialog.cs, ShrinkOfferDialog.cs, DiagnosticsDialog.cs
└── Resources/Strings*.resx           # About_IconCredit

tests/HddBackup.Core.Tests/AppIconFileTests.cs   # .ico entries
THIRD-PARTY-NOTICES.md
```

**Structure Decision**: artwork and its build script live in `assets/icon/` (developer-only, like
the ext4 fixture scripts); the program consumes only the committed `app.ico`.

## Complexity Tracking

No constitution violations.
