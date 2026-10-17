# Quickstart: Validating the Application Icon

## Rebuild the icon (only when the artwork changes)

```bash
docker run --rm -v "$PWD:/w" -w /w debian:bookworm bash assets/icon/make-icon.sh
```

Expected: `src/HddBackup.App/app.ico` with 8 entries (16, 20, 24, 32, 40, 48, 64, 256).

## Automated checks

```powershell
dotnet build HddBackup.sln -c Release
dotnet test  tests/HddBackup.Core.Tests -c Release
```

Expected: 0 warnings; all tests pass, including the icon entry test. Executable size grows by at
most 200 KB compared with main (SC-003).

## Manual scenarios

| # | Scenario | Expected | Spec |
|---|----------|----------|------|
| 1 | Explorer: small, medium, large, extra large icons on `HddBackup.exe` | Blue SD card at every size, sharp | US1 |
| 2 | Start the program | Same icon in the title bar, taskbar and Alt+Tab | US1 |
| 3 | 150% and 200% display scaling | Title bar and taskbar icons sharp | US1 |
| 4 | Dark taskbar (Windows 11 dark mode) and light | Clearly visible on both | Edge case |
| 5 | Open About, the write confirmation, the shrink offer, diagnostics | Program icon in each title bar; About shows the large icon and the credit line | US2 |

## Results

| Date | Build / tests | Scenarios | Notes |
|------|---------------|-----------|-------|
| | | | |
