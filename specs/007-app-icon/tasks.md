---

description: "Task list for feature 007: application icon"
---

# Tasks: Application Icon

**Input**: Design documents from `/specs/007-app-icon/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/ui.md, quickstart.md

**Tests**: one unit test of the `.ico` entries (research R-4); the rest is checked with the
manual scenarios in quickstart.md.

**Organization**: US1 icon in Explorer/taskbar/title bar (P1), US2 same icon in every window and
in About (P2).

---

## Phase 1: Setup — artwork and icon file

- [ ] T001 Download `Papirus/<N>x<N>/devices/media-flash.svg` for N = 16, 22, 24, 32, 48, 64 from `PapirusDevelopmentTeam/papirus-icon-theme` (record the commit SHA of the default branch) into `assets/icon/papirus/media-flash-<N>x<N>.svg`, unchanged
- [ ] T002 Create the recolored copies `assets/icon/media-flash-<N>x<N>.svg`: replace `#4f4f4f` and `#444444` with `#2f6db5`, and insert after the `<svg>` tag an XML comment naming the origin (Papirus icon theme, Papirus Development Team, upstream path and commit), the license (GPL-3.0-only as in the upstream repository) and the change (body color)
- [ ] T003 Create `assets/icon/make-icon.sh` (bash, run in `debian:bookworm`, installs `librsvg2-bin icoutils`): render 16←16, 20←22, 24←24, 32←32, 40←48, 48←48, 64←64, 256←64 with `rsvg-convert -w N -h N`, pack with `icotool -c -o src/HddBackup.App/app.ico <16..64 pngs> --raw=<256 png>`, then print `icotool -l` of the result; usage comment like the ext4 fixture scripts
- [ ] T004 Create `assets/icon/README.md`: origin, license, upstream commit, the change, how to rebuild (`docker run ... bash assets/icon/make-icon.sh`)
- [ ] T005 Run `make-icon.sh` in Docker and commit `src/HddBackup.App/app.ico`; record its size

---

## Phase 2: Foundational

- [ ] T006 [P] Create `tests/HddBackup.Core.Tests/AppIconFileTests.cs`: locate the solution directory upwards from `AppDomain.CurrentDomain.BaseDirectory` (directory containing `HddBackup.sln`), parse `src/HddBackup.App/app.ico` (ICONDIR: reserved 0, type 1, count; ICONDIRENTRY: width/height byte, 0 = 256; bit count; offset), assert the sizes are exactly {16, 20, 24, 32, 40, 48, 64, 256}, entries ≤ 64 are 32-bit BMP (`BITMAPINFOHEADER` size 40, bit count 32) and 256 is PNG (signature `89 50 4E 47`), and the file is under 100 KB
- [ ] T007 Create `src/HddBackup.App/Forms/AppIcon.cs`: `internal static class AppIcon` with a lazily loaded `public static Icon Icon` from the embedded resource stream `HddBackup.App.app.ico` (exact name from `GetManifestResourceNames`), and `public static Bitmap Image(int size)` returning `new Icon(Icon, size, size).ToBitmap()`

---

## Phase 3: User Story 1 - Icon in Explorer, taskbar and title bar (Priority: P1) 🎯 MVP

**Goal**: the executable and the main window carry the icon at every size.

**Independent Test**: Explorer views of the built exe; running program's title bar, taskbar,
Alt+Tab at 100% and 150%.

- [ ] T008 [US1] In `src/HddBackup.App/HddBackup.App.csproj` add `<ApplicationIcon>app.ico</ApplicationIcon>` to the main PropertyGroup and `<EmbeddedResource Include="app.ico" LogicalName="HddBackup.App.app.ico" />`
- [ ] T009 [US1] In `src/HddBackup.App/Forms/MainForm.cs` replace `Icon.ExtractAssociatedIcon(Application.ExecutablePath)` with `AppIcon.Icon`
- [ ] T010 [US1] Build; compare the executable size with main (≤ 200 KB growth, SC-003); run T006; check that `Icon.ExtractAssociatedIcon` on the built exe returns the new icon (not the generic one) and check quickstart scenarios 1–4 visually (Explorer views, title bar, taskbar, Alt+Tab, 150%)

**Checkpoint**: MVP — the program is recognizable everywhere.

---

## Phase 4: User Story 2 - Same icon in every window, large icon in About (Priority: P2)

**Goal**: dialogs show the program icon; About shows it large with a credit line.

**Independent Test**: open each dialog; About shows the icon and the credit.

- [ ] T011 [P] [US2] Add `About_IconCredit` to all six `src/HddBackup.App/Resources/Strings*.resx`: en "Icon: Papirus icon theme (GPL-3.0), card color changed.", uk "Іконка: тема іконок Papirus (GPL-3.0), колір карти змінено.", de "Symbol: Papirus-Symbolthema (GPL-3.0), Kartenfarbe geändert.", fr "Icône : thème d'icônes Papirus (GPL-3.0), couleur de la carte modifiée.", es "Icono: tema de iconos Papirus (GPL-3.0), color de la tarjeta cambiado.", ja "アイコン: Papirus アイコン テーマ (GPL-3.0)、カードの色を変更。"
- [ ] T012 [US2] Set `Icon = AppIcon.Icon` in the constructors of `src/HddBackup.App/Forms/AboutDialog.cs`, `ConfirmWriteDialog.cs`, `ShrinkOfferDialog.cs` and `DiagnosticsDialog.cs`
- [ ] T013 [US2] In `src/HddBackup.App/Forms/AboutDialog.cs` put a `PictureBox` (SizeMode Zoom) with `AppIcon.Image(64 × DeviceDpi / 96, nearest of 64/128/256)` to the left of the program name (two-column table: icon, then the existing name/text/license labels), and add a label with `About_IconCredit` under the license text
- [ ] T014 [US2] Build; screenshot each dialog (in-process harness as in features 005/006) in English and Japanese; check quickstart scenario 5

---

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T015 [P] Add a "Papirus icon theme" section to `THIRD-PARTY-NOTICES.md` (project, authors, GPL-3.0, source URL and commit, the change, location `assets/icon/`); mention the icon in `docs/release-notes.md` (Unreleased)
- [ ] T016 Run all tests and the string key check; record results in `specs/007-app-icon/quickstart.md`

---

## Dependencies & Execution Order

- Phase 1 → T006/T007 → US1 → US2 → Polish.
- T006 and T007 are independent; T011 is independent of code tasks.

### Parallel Opportunities

- T006 with T007; T011 with T012; T015 with T014.

## Implementation Strategy

1. Phase 1–2: artwork, icon file, test, `AppIcon`.
2. US1 (MVP): executable and main window icon.
3. US2: dialogs and About.
4. Polish: notices, results.
