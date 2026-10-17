# UI Contract: Application Icon

| Place | Behavior |
|-------|----------|
| Explorer, shortcuts, taskbar, Alt+Tab | Win32 icon resource of `HddBackup.exe` (`ApplicationIcon`) |
| Main window title bar | `Icon = AppIcon.Icon` (multi-size; Windows picks 16/20/24 by DPI) |
| Dialogs (About, confirm write, shrink offer, diagnostics) | `Icon = AppIcon.Icon` |
| About window | Icon at 64 px × DPI scale left of the program name; credit line under the license text |
| Message boxes | Unchanged (system icons) |

## Strings (six languages)

| Key | English |
|-----|---------|
| `About_IconCredit` | "Icon: Papirus icon theme (GPL-3.0), card color changed." |
