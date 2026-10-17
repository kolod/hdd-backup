#!/bin/bash
# Builds ext4 test fixtures with the real e2fsprogs. Developer tooling only: run in Docker
# (debian:bookworm); the product never uses Linux tools.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" \
#              -v "<repo>/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
#              debian:bookworm bash /w/scripts/make-fixtures.sh
#
# Outputs (in /w/out):
#   rpi-like-4k.img.gz / .manifest.tsv   MBR: FAT32 boot + ext4 root (Raspberry Pi OS features, 4 KiB blocks)
#   rpi-like-1k.img.gz / .manifest.tsv   same content subset, 1 KiB blocks
#   dirty.img.gz                         root ext4 that needs journal recovery (negative)
#   inline.img.gz                        root ext4 with inline_data (negative)
#   root-not-last.img.gz                 ext4 partition followed by another partition (negative)
#   layout-reference.txt                 dumpe2fs geometry for layout tests
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs dosfstools mtools fdisk attr acl libcap2-bin >/dev/null

OUT=/w/out
WORK=/tmp/work
rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

export E2FSPROGS_FAKE_TIME=1760000000   # deterministic superblock timestamps
FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,64bit,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,metadata_csum"
UUID=11111111-2222-3333-4444-555555555555
SEED=66666666-7777-8888-9999-aaaaaaaaaaaa
MIB=$((1024 * 1024))

# Shared helpers: content, stage_root, manifest, make_boot, assemble, fragment, build_rpi_like.
# shellcheck source=fixture-lib.sh
source "$(dirname "$0")/fixture-lib.sh"

BIG_XATTR=3000 build_rpi_like rpi-like-4k 4096 256 24 6144
BIG_XATTR=700 build_rpi_like rpi-like-1k 1024 128 16 3072
# Older Raspbian (stretch era): no 64bit, no metadata_csum, uninit_bg (crc16 group checksums).
LEGACY="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,flex_bg,sparse_super,large_file,huge_file,uninit_bg,dir_nlink,extra_isize"
BIG_XATTR=3000 build_rpi_like rpi-legacy 4096 128 8 4096 "$LEGACY"
# GPT partition table instead of MBR.
LABEL=gpt BIG_XATTR=3000 build_rpi_like rpi-like-gpt 4096 128 8 4096

# Negative: needs journal recovery (as if the card was pulled while mounted).
make_boot "$WORK/neg-boot.vfat" 64 32
mke2fs -q -F -t ext4 -O "$FEATURES" -U "$UUID" "$WORK/dirty.ext4" 32M
debugfs -w -R "feature needs_recovery" "$WORK/dirty.ext4" >/dev/null 2>&1
assemble "$WORK/dirty.img" "$WORK/neg-boot.vfat" "$WORK/dirty.ext4"
gzip -9 -c "$WORK/dirty.img" > "$OUT/dirty.img.gz"

# Negative: unsupported feature.
mke2fs -q -F -t ext4 -O "$FEATURES,inline_data" "$WORK/inline.ext4" 32M
assemble "$WORK/inline.img" "$WORK/neg-boot.vfat" "$WORK/inline.ext4"
gzip -9 -c "$WORK/inline.img" > "$OUT/inline.img.gz"

# Negative: root is not the last partition.
mke2fs -q -F -t ext4 -O "$FEATURES" "$WORK/mid.ext4" 32M
make_boot "$WORK/extra.vfat" 64 32
assemble "$WORK/root-not-last.img" "$WORK/neg-boot.vfat" "$WORK/mid.ext4" "$WORK/extra.vfat"
gzip -9 -c "$WORK/root-not-last.img" > "$OUT/root-not-last.img.gz"

# Geometry references for layout tests (sparse files; no data written).
{
    for spec in "64:4096" "448:4096" "4096:4096" "16384:4096" "128:1024" "448:1024"; do
        mib=${spec%%:*}; bs=${spec##*:}
        f=$WORK/ref.ext4; rm -f "$f"
        mke2fs -q -F -t ext4 -b "$bs" -I 256 -i 16384 -O "$FEATURES" "$f" "${mib}M"
        echo "=== size=${mib}MiB block=$bs"
        dumpe2fs "$f" 2>/dev/null | grep -E '^(Block count|Inode count|Blocks per group|Inodes per group|Reserved GDT blocks|First block|Flex block group size|Inode size|Journal size|Inode blocks per group)|^Group [0-9]+:|superblock at|Block bitmap at|Inode bitmap at|Inode table at'
    done
} > "$OUT/layout-reference.txt"

ls -l "$OUT"
