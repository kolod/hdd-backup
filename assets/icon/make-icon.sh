#!/bin/bash
# Builds src/HddBackup.App/app.ico from the SVGs in assets/icon/ (feature 007). Developer tooling
# only: run in Docker; the product uses the committed app.ico.
#
#   docker run --rm -v "<repo>:/w" -w /w debian:bookworm bash assets/icon/make-icon.sh
#
# Each size is rendered from the Papirus drawing made for the nearest size at or above it, so
# small sizes keep their hand-tuned details. 16-64 px are stored as 32-bit BMP entries, 256 px as
# a PNG entry (Windows Vista and later).
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
{ command -v rsvg-convert && command -v icotool; } >/dev/null || {
    apt-get update -qq >/dev/null
    apt-get install -y -qq librsvg2-bin icoutils >/dev/null
}

HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/../../src/HddBackup.App/app.ico"
WORK=$(mktemp -d)

source_for() { # target px -> svg drawn for the nearest size at or above it
    for s in 16 22 24 32 48 64; do
        if [ "$1" -le "$s" ]; then echo "$HERE/media-flash-${s}x${s}.svg"; return; fi
    done
    echo "$HERE/media-flash-64x64.svg"
}

small=()
for px in 16 20 24 32 40 48 64; do
    rsvg-convert -w "$px" -h "$px" "$(source_for "$px")" -o "$WORK/$px.png"
    small+=("$WORK/$px.png")
done
rsvg-convert -w 256 -h 256 "$(source_for 256)" -o "$WORK/256.png"

icotool -c -o "$OUT" "${small[@]}" --raw="$WORK/256.png"
icotool -l "$OUT"
echo "app.ico: $(stat -c %s "$OUT") bytes"
