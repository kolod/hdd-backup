# Feature Specification: Application Icon

**Feature Branch**: `007-app-icon`

**Created**: 2026-10-17

**Status**: Draft (icon approved 2026-10-17)

**Input**: User description: "Application icon."

## Clarifications

### Session 2026-10-17

- Q: Concept of the icon? → A: Use a ready-made icon with an open license: the SD card
  `media-flash` of the Papirus icon theme (GPL-3.0, the same license as this program), with the
  card body recolored from dark grey to blue (`#2f6db5`) so it stays visible on dark taskbars. The
  hand-drawn sizes of Papirus (16, 22, 24, 32, 48, 64 px) are kept. Approved preview:
  [preview.png](preview.png) (light and dark background, 16–256 px).

## Context

The program has no icon of its own: `HddBackup.exe` shows the generic Windows program icon in
Explorer, on the taskbar, in Alt+Tab and in the window title bar, and its dialogs show the generic
window icon. Users who keep the portable executable on a USB stick or desktop among other tools
cannot recognize it at a glance.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Recognize the program in Explorer and on the taskbar (Priority: P1)

A user copies `HddBackup.exe` to a USB stick or the desktop. In Explorer (any view from small
icons to extra large icons), on the taskbar, in Alt+Tab and in the title bar, the program shows its
own icon that suggests what it does (memory cards / USB drives and their images), clearly
different from the generic program icon.

**Why this priority**: this is the whole point of the feature.

**Independent Test**: build the executable and look at it in Explorer in all icon views, start it
and look at the taskbar, title bar and Alt+Tab.

**Acceptance Scenarios**:

1. **Given** the built executable, **When** Explorer shows it in small, medium, large and extra
   large icon views, **Then** the program icon is shown, sharp at every size.
2. **Given** the program is running, **When** the user looks at the title bar, the taskbar and
   Alt+Tab, **Then** the same icon is shown.
3. **Given** display scaling of 100%, 150% or 200%, **When** the icon is shown anywhere, **Then**
   it is sharp (no blurry upscaling of a small image).

---

### User Story 2 - Same icon in every window of the program (Priority: P2)

Every window the program opens (confirmation before writing, shrink offer, diagnostics, About)
shows the program icon in its title bar where Windows shows one, and the About window shows a
larger version of the icon next to the program name.

**Why this priority**: consistency; the dialogs currently show the generic window icon.

**Independent Test**: open each dialog and check its title bar; open About.

**Acceptance Scenarios**:

1. **Given** any dialog of the program, **When** it opens, **Then** its title bar shows the
   program icon or no icon (Windows style for fixed dialogs), never the generic window icon.
2. **Given** the About window, **When** it opens, **Then** the icon is shown at a large size next
   to the program name and version.

### Edge Cases

- **Dark and light taskbar / Explorer themes** (Windows 10/11) and the classic Windows 7 theme:
  the icon stays clearly visible on light and dark backgrounds.
- **High contrast mode**: the icon remains recognizable by its shape.
- **Windows 7 icon cache**: an old cached icon may be shown for a copied executable until the cache
  refreshes; this is Windows behavior and not a defect.
- **Executable size**: the icon must not noticeably grow the single portable executable.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The executable MUST carry the program icon, so Explorer, shortcuts and the taskbar
  show it.
- **FR-002**: The icon MUST contain images for the sizes Windows uses — 16, 20, 24, 32, 40, 48,
  64 and 256 pixels — each drawn for its size so that small sizes stay sharp and readable.
- **FR-003**: The main window and every dialog with a title bar icon MUST use the program icon.
- **FR-004**: The About window MUST show the icon at a large size next to the program name.
- **FR-005**: The icon MUST stay recognizable on light, dark and high-contrast backgrounds.
- **FR-006**: The icon MUST be the Papirus `media-flash` SD card with the body recolored to blue
  (`#2f6db5`), as in the approved preview; each size MUST use the Papirus drawing made for the
  nearest size.
- **FR-008**: The origin (Papirus icon theme, its authors, GPL-3.0, source link) and the
  modification (body color) MUST be stated in `THIRD-PARTY-NOTICES.md` and in the About window.
- **FR-007**: The source of the icon MUST be kept in the repository in an editable form, and the
  icon file MUST be reproducible from it, so the icon can be adjusted later.

### Key Entities

- **Program icon**: one design rendered at several sizes in a single icon resource, used by the
  executable and all windows.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The program is shown with its own icon in 100% of the places Windows shows program
  icons (Explorer views, taskbar, Alt+Tab, title bars, shortcuts).
- **SC-002**: At 16 × 16 pixels the icon is recognizable as the same design as at 256 × 256 (checked
  side by side on the preview sheet).
- **SC-003**: The executable grows by no more than 200 KB.
- **SC-004**: No window of the program shows the generic Windows window icon.

## Assumptions

- The icon is taken from the Papirus icon theme under GPL-3.0, which is the program's own license,
  so the combination needs no license change; modified files keep the GPL-3.0 notice.
- One icon for all languages; it contains no text.
- The Windows 7 – 11 range applies; no separate icon sets per Windows version.
