# Application icon

`src/HddBackup.App/app.ico` is built from the SVG files in this folder.

## Origin and license

The icon is the SD card `media-flash` of the [Papirus icon theme](https://github.com/PapirusDevelopmentTeam/papirus-icon-theme)
by the Papirus Development Team, licensed under GPL-3.0 (the same license as HddBackup).

- `papirus/media-flash-<N>x<N>.svg`: the upstream files, unchanged, from
  `Papirus/<N>x<N>/devices/media-flash.svg` at commit `bf539287ef5dc18529424a02cccee76175920a6f`.
- `media-flash-<N>x<N>.svg`: the files used for the build. **Modified for HddBackup**: the card body
  color (`#4f4f4f`, and `#444444` in the 16 px symbolic drawing) is changed to `#2f6db5` so the
  icon stays visible on dark taskbars. Contacts, highlights and shadows are unchanged.

Papirus draws each size separately (16, 22, 24, 32, 48, 64 px); every icon size is rendered from
the drawing for the nearest size at or above it.

## Rebuild

```bash
docker run --rm -v "$PWD:/w" -w /w debian:bookworm bash assets/icon/make-icon.sh
```

The script prints the entries of the new `app.ico` (16, 20, 24, 32, 40, 48, 64 px as 32-bit
BMP, 256 px as PNG). Commit the updated `app.ico`; the normal build does not need Docker.
