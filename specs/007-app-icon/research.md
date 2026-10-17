# Research: Application Icon

## R-1. Source artwork and its license

- **Decision**: the Papirus icon theme's `Papirus/<N>x<N>/devices/media-flash.svg` for N = 16, 22,
  24, 32, 48 and 64 (hand-drawn per size; `media-flash-sd-mmc` is an alias of it). Copies are kept
  in `assets/icon/papirus/` unchanged, with the upstream commit recorded; the recolored versions
  used for the build are in `assets/icon/` with a comment naming the origin, the license and the
  change.
- **License**: Papirus is GPL-3.0 (repository license checked 2026-10-17), the same license as
  HddBackup; modified files keep the GPL-3.0 notice. `THIRD-PARTY-NOTICES.md` names the project,
  its authors ("Papirus Development Team"), the license, the source URL and the change.
- **Recolor**: the body color `#4f4f4f` (22–64 px) and the 16 px symbolic color `#444444` become
  `#2f6db5`; contacts (`#f8c44e`), highlights and shadows are unchanged. Approved in
  [preview.png](preview.png).

## R-2. Building the .ico

- **Decision**: `assets/icon/make-icon.sh` (Docker, `debian:bookworm`, `librsvg2-bin`,
  `icoutils`) renders each target size from the SVG drawn for the nearest size at or above it
  (16←16, 20←22, 24←24, 32←32, 40←48, 48←48, 64←64, 256←64 as vector) and packs them with
  `icotool -c`: 16–64 px as 32-bit BMP entries, 256 px as a PNG entry (`--raw`). Output:
  `src/HddBackup.App/app.ico`, committed, so normal builds need no Docker (developer-only tooling,
  constitution "Technology").
- **Rationale**: BMP entries for small sizes are what every Windows version loads fastest; a PNG
  256 px entry is supported since Windows Vista and keeps the file small (SC-003).
- **Alternatives considered**: rendering in .NET at build time (needs an SVG renderer — a new
  dependency); ImageMagick `convert` to ICO (stores all sizes as PNG or all as BMP; no control).

## R-3. Using the icon in the program

- **Decision**:
  - `HddBackup.App.csproj`: `<ApplicationIcon>app.ico</ApplicationIcon>` (Win32 icon resource:
    Explorer, shortcuts, taskbar) and the same file as an embedded resource.
  - `AppIcon` (App, `Forms/AppIcon.cs`): loads the embedded multi-size icon once;
    `MainForm` and every dialog (`AboutDialog`, `ConfirmWriteDialog`, `ShrinkOfferDialog`,
    `DiagnosticsDialog`) set `Icon = AppIcon.Icon`, so Windows picks the right size for the title
    bar (16/20/24) and Alt+Tab/taskbar (32/48) at any DPI.
  - About: a `PictureBox` with the icon at 64 px × DPI scale (from the multi-size icon:
    `new Icon(AppIcon.Icon, size).ToBitmap()`), and a credit line `About_IconCredit` in six
    languages.
- **Rationale**: `Icon.ExtractAssociatedIcon` (used today) returns only the 32 px image, which is
  scaled down blurrily in the title bar; the embedded multi-size icon avoids that (US1 scenario 3).

## R-4. Verification

- A unit test reads `src/HddBackup.App/app.ico` (located from the test assembly upwards to the
  solution) and checks the entries: sizes 16, 20, 24, 32, 40, 48, 64, 256; 32-bit; 256 as PNG.
- Screenshots of Explorer views and the running program at 100% and 150% (quickstart).
