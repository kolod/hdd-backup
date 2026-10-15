#!/bin/bash
# Independent check of a shrunk image against its source, with the real e2fsprogs.
# Developer tooling only (Docker); the product never uses Linux tools.
#
#   docker run --rm -v "<repo>:/w" debian:bookworm bash /w/tests/fixtures/ext4/check-output.sh \
#          /w/out/shrunk.img /w/tests/HddBackup.Core.Tests/Fixtures/ext4/rpi-like-4k.img.gz
#
# Exit code 0 only if: e2fsck -fn is clean, and every object (content, mode, owner, times,
# symlink target, device numbers, xattrs, hard-link grouping) matches the source.
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
command -v e2fsck >/dev/null || { apt-get update -qq >/dev/null; apt-get install -y -qq e2fsprogs fdisk attr >/dev/null; }

OUTPUT=$1
SOURCE=$2
WORK=$(mktemp -d)

extract_last() { # image (or bare ext4 file) -> path of last partition as a file
    local img=$1 name=$2 start size
    if ! sfdisk -d "$img" >/dev/null 2>&1; then echo "$img"; return; fi
    read -r start size < <(sfdisk -d "$img" | grep -E 'start=' | tail -1 | sed -E 's/.*start= *([0-9]+), size= *([0-9]+).*/\1 \2/')
    dd if="$img" of="$WORK/$name.ext4" bs=512 skip="$start" count="$size" status=none
    echo "$WORK/$name.ext4"
}

src_img=$SOURCE
if [[ "$SOURCE" == *.gz ]]; then gunzip -c "$SOURCE" > "$WORK/source.img"; src_img=$WORK/source.img; fi

out_fs=$(extract_last "$OUTPUT" out)
src_fs=$(extract_last "$src_img" src)

echo "--- e2fsck -fn (output)"
e2fsck -fn "$out_fs"

echo "--- tree comparison"
# Per-path metadata straight from the inodes (independent of rdump's permission handling).
inode_list() { # fs
    local fs=$1
    local dump="$WORK/dump-$(basename "$fs")"
    mkdir -p "$dump"
    debugfs -R "rdump / $dump" "$fs" >/dev/null 2>&1
    # rdump of "/" may create an extra level; descend into it if so.
    [ -d "$dump/root" ] && [ "$(ls -A "$dump" | wc -l)" = 1 ] && dump="$dump/root"
    (cd "$dump" && find . -mindepth 1 -not -path './lost+found*' -printf '%P\n' | LC_ALL=C sort) |
    while IFS= read -r p; do
        # Expected to differ after a rebuild: inode number (and the inode checksum derived from
        # it), flags (htree index), block count, and the byte size of directories.
        raw=$(debugfs -R "stat \"/$p\"" "$fs" 2>/dev/null)
        stat_out=$(echo "$raw" | grep -E '^(Inode|User|Links)|time:' | grep -v '^Inode checksum' | sed -E 's/Inode: [0-9]+ +//; s/Flags: 0x[0-9a-f]+//; s/Blockcount: [0-9]+//; s/  +/ /g')
        # No `echo | grep -q` here: with pipefail, grep -q exiting early can make echo fail with
        # SIGPIPE and the test randomly false (seen in CI).
        if [[ $raw == *'Type: directory'* ]]; then stat_out=$(echo "$stat_out" | sed -E 's/Size: [0-9]+//'); fi
        xa=$(debugfs -R "ea_list \"/$p\"" "$fs" 2>/dev/null | tr '\n' ' ')
        sum=-
        [ -f "$dump/$p" ] && [ ! -L "$dump/$p" ] && sum=$(sha256sum "$dump/$p" | cut -d' ' -f1)
        printf '%s | %s | %s | %s\n' "$p" "$(echo "$stat_out" | tr '\n' ' ')" "$xa" "$sum"
    done
}

inode_list "$src_fs" > "$WORK/src.list"
inode_list "$out_fs" > "$WORK/out.list"
if diff -u "$WORK/src.list" "$WORK/out.list"; then
    echo "OK: $(wc -l < "$WORK/src.list") objects identical"
else
    echo "DIFFERENCES FOUND" >&2
    exit 1
fi
