#!/bin/bash
# Builds the SC-004 performance image: 16 GiB, MBR with FAT32 boot + ext4 root (Raspberry Pi OS
# features), about 3 GiB of incompressible data in many files. Not committed; developer only.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" -v "<out dir>:/w/out" \
#              debian:bookworm bash /w/scripts/make-large.sh
#
# Output: /w/out/large-16g.img.gz (gzip -1, about 3 GiB)
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs dosfstools mtools fdisk >/dev/null

OUT=/w/out
WORK=/tmp/large
rm -rf "$WORK" && mkdir -p "$WORK/stage" "$OUT"
FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,64bit,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,metadata_csum"
TOTAL_MIB=16384
BOOT_MIB=256

# 2,800 files of 1 MiB in 28 directories, 400 files of 1 MiB..8 MiB, 30,000 small files.
for d in $(seq 1 28); do
    mkdir -p "$WORK/stage/usr/share/d$d"
    for f in $(seq 1 100); do head -c 1048576 /dev/urandom > "$WORK/stage/usr/share/d$d/f$f.bin"; done
done
mkdir -p "$WORK/stage/opt/big"
for f in $(seq 1 40); do head -c $(( (f % 8 + 1) * 1048576 )) /dev/urandom > "$WORK/stage/opt/big/b$f.bin"; done
for d in $(seq 1 300); do
    mkdir -p "$WORK/stage/etc/small/s$d"
    for f in $(seq 1 100); do head -c $(( (f * 37) % 4000 + 10 )) /dev/urandom > "$WORK/stage/etc/small/s$d/c$f"; done
done
du -sh "$WORK/stage"

ROOT_MIB=$(( TOTAL_MIB - 1 - BOOT_MIB ))
mkfs.vfat -F 32 -n BOOT -C "$WORK/boot.vfat" $(( BOOT_MIB * 1024 )) >/dev/null
mke2fs -q -F -t ext4 -b 4096 -I 256 -i 16384 -O none -O "$FEATURES" -L rootfs -d "$WORK/stage" "$WORK/root.ext4" "${ROOT_MIB}M"
e2fsck -fn "$WORK/root.ext4" >/dev/null

truncate -s "${TOTAL_MIB}M" "$WORK/large.img"
{
    echo "label: dos"
    echo "start=2048, size=$(( BOOT_MIB * 2048 )), type=c"
    echo "start=$(( 2048 + BOOT_MIB * 2048 )), size=$(( ROOT_MIB * 2048 )), type=83"
} | sfdisk -q "$WORK/large.img"
dd if="$WORK/boot.vfat" of="$WORK/large.img" bs=1M seek=1 conv=notrunc,sparse status=none
dd if="$WORK/root.ext4" of="$WORK/large.img" bs=1M seek=$(( 1 + BOOT_MIB )) conv=notrunc,sparse status=none
gzip -1 -c "$WORK/large.img" > "$OUT/large-16g.img.gz"
ls -l "$OUT"
