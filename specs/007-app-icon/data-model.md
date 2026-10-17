# Data Model: Application Icon

## Icon file (`src/HddBackup.App/app.ico`)

| Entry | Size (px) | Source SVG | Format |
|-------|-----------|------------|--------|
| 1 | 16 | 16×16 | 32-bit BMP |
| 2 | 20 | 22×22 | 32-bit BMP |
| 3 | 24 | 24×24 | 32-bit BMP |
| 4 | 32 | 32×32 | 32-bit BMP |
| 5 | 40 | 48×48 | 32-bit BMP |
| 6 | 48 | 48×48 | 32-bit BMP |
| 7 | 64 | 64×64 | 32-bit BMP |
| 8 | 256 | 64×64 (vector) | PNG |

## Assets (`assets/icon/`)

| File | Content |
|------|---------|
| `papirus/media-flash-<N>x<N>.svg` | Upstream Papirus files, unchanged (N = 16, 22, 24, 32, 48, 64) |
| `media-flash-<N>x<N>.svg` | Recolored copies used for the build (`#4f4f4f`/`#444444` → `#2f6db5`), with an origin/license comment |
| `make-icon.sh` | Regenerates `app.ico` (Docker); prints the entry table |
| `README.md` | Origin (Papirus, upstream commit), license, change, how to rebuild |
