#!/bin/bash
# Builds the ext4 fixtures of feature 010 (editing files). Developer tooling only: run in Docker
# (debian:bookworm, e2fsprogs 1.47); the product never uses Linux tools.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" \
#              -v "<repo>/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
#              debian:bookworm bash /w/scripts/make-editing-fixtures.sh
#
# Outputs (in /w/out), bare ext4 images (single partition, no table), each with <name>.manifest.tsv:
#   edit-basic.img.gz     4 KiB blocks, metadata_csum, flex_bg: text files (LF, CRLF, no final newline),
#                         an empty file, a 300 KiB file, a binary file, fast and slow symlinks, hard links,
#                         a file with an xattr block, nested directories, and /many (600 files) made
#                         htree-indexed with e2fsck -D
#   edit-1k.img.gz        1 KiB blocks, same kind of content, smaller
#   edit-nocsum.img.gz    4 KiB blocks, uninit_bg group checksums only (no metadata_csum)
#   edit-full.img.gz      a 4 MiB file system with only a few free blocks
#   edit-uninit.img.gz    256 MiB with 1 KiB blocks: most groups BLOCK_UNINIT / INODE_UNINIT
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs attr acl libcap2-bin python3 >/dev/null

OUT=/w/out
WORK=/tmp/edit
rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

export E2FSPROGS_FAKE_TIME=1760000000
BASE_FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize"
UUID=11111111-2222-3333-4444-555555555558
SEED=66666666-7777-8888-9999-aaaaaaaaaaad
MIB=$((1024 * 1024))

# shellcheck source=fixture-lib.sh
source "$(dirname "$0")/fixture-lib.sh"

stage_tree() { # dir
    local d=$1
    rm -rf "$d"; mkdir -p "$d"/{etc,home/pi,many,nested/a/b/c,var/log}
    printf 'raspberrypi\n' > "$d/etc/hostname"
    content fstab 400 > "$d/etc/fstab"
    printf 'line one\r\nline two\r\nline three\r\n' > "$d/etc/crlf.txt"
    printf 'no final newline' > "$d/etc/nonl.txt"
    : > "$d/etc/empty"
    content big 307200 > "$d/home/pi/big.txt"
    { content bin-a 3000; head -c 64 /dev/zero; content bin-b 3000; } > "$d/home/pi/tool.bin"
    ln -s hostname "$d/etc/fast-link"
    ln -s "/$(printf 'very-long-target-%.0s' $(seq 1 10))/end" "$d/etc/slow-link"
    content hard 5000 > "$d/home/pi/a"
    ln "$d/home/pi/a" "$d/home/pi/b"
    content xattr 900 > "$d/etc/with-xattr"
    setfattr -n user.comment -v "hello xattr" "$d/etc/with-xattr"
    setfattr -n user.big -v "$(head -c 600 /dev/zero | tr '\0' 'x')" "$d/etc/with-xattr"
    content deep 700 > "$d/nested/a/b/c/deep.txt"
    content mid 300 > "$d/nested/a/mid.txt"
    for i in $(seq 1 600); do printf 'f%04d\n' "$i" > "$d/many/entry-with-a-longer-name-$i.conf"; done
    chown 1000:1000 "$d/home/pi" "$d/home/pi/a"
    chmod 0640 "$d/etc/fstab"
    touch -d '2024-01-02 03:04:05' "$d/etc/hostname" "$d/etc/fstab"
}

mkfs_dir() { # name stage size features blocksize [extra]
    local name=$1 stage=$2 size=$3 features=$4 bs=$5
    shift 5
    rm -f "$WORK/$name.ext4"
    mke2fs -q -F -t ext4 -b "$bs" -I 256 -O none -O "$features" -L edit \
        -U "$UUID" -E "hash_seed=$SEED,root_owner=0:0" "$@" -d "$stage" "$WORK/$name.ext4" "$size"
}

check() { # name
    e2fsck -fn "$WORK/$1.ext4" > "$WORK/$1-fsck.txt" 2>&1 || { cat "$WORK/$1-fsck.txt"; exit 1; }
}

publish() { # name stage
    manifest "$2" > "$OUT/$1.manifest.tsv"
    gzip -9 -n -c "$WORK/$1.ext4" > "$OUT/$1.img.gz"
    echo "built $1: $(stat -c %s "$WORK/$1.ext4") bytes, $(wc -l < "$OUT/$1.manifest.tsv") manifest entries"
}

# --- edit-basic ---------------------------------------------------------------------------------
S=$WORK/basic
stage_tree "$S"
mkfs_dir edit-basic "$S" 32M "$BASE_FEATURES,64bit,metadata_csum" 4096
e2fsck -fyD "$WORK/edit-basic.ext4" > "$WORK/optimize.txt" 2>&1 || true
check edit-basic
debugfs -R "htree_dump /many" "$WORK/edit-basic.ext4" 2>&1 | head -3 || true
publish edit-basic "$S"

# --- edit-1k ------------------------------------------------------------------------------------
S=$WORK/one
stage_tree "$S"
rm -rf "$S/many"; mkdir "$S/many"
for i in $(seq 1 120); do printf 'f%04d\n' "$i" > "$S/many/entry-with-a-longer-name-$i.conf"; done
mkfs_dir edit-1k "$S" 16M "$BASE_FEATURES,metadata_csum" 1024
check edit-1k
publish edit-1k "$S"

# --- edit-nocsum --------------------------------------------------------------------------------
S=$WORK/nocsum
stage_tree "$S"
mkfs_dir edit-nocsum "$S" 32M "$BASE_FEATURES,uninit_bg" 4096
check edit-nocsum
publish edit-nocsum "$S"

# --- edit-full ----------------------------------------------------------------------------------
S=$WORK/full
rm -rf "$S"; mkdir -p "$S/etc"
printf 'raspberrypi\n' > "$S/etc/hostname"
content fstab 400 > "$S/etc/fstab"
mkfs_dir edit-full "$S" 4M "ext_attr,dir_index,filetype,extent,sparse_super,large_file,huge_file,dir_nlink,extra_isize,uninit_bg" 1024
FREE=$(dumpe2fs -h "$WORK/edit-full.ext4" 2>/dev/null | awk '/^Free blocks:/ {print $3}')
head -c $(( (FREE - 6) * 1024 )) /dev/zero | tr '\0' 'z' > "$WORK/filler"
debugfs -w -R "write $WORK/filler filler" "$WORK/edit-full.ext4" >/dev/null 2>&1
cp "$WORK/filler" "$S/filler"
check edit-full
echo "edit-full free blocks left: $(dumpe2fs -h "$WORK/edit-full.ext4" 2>/dev/null | awk '/^Free blocks:/ {print $3}')"
publish edit-full "$S"

# --- edit-uninit --------------------------------------------------------------------------------
S=$WORK/uninit
rm -rf "$S"; mkdir -p "$S/etc"
printf 'raspberrypi\n' > "$S/etc/hostname"
content fstab 400 > "$S/etc/fstab"
mkfs_dir edit-uninit "$S" 256M "ext_attr,resize_inode,dir_index,filetype,extent,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,uninit_bg" 1024 -E lazy_itable_init=1,"hash_seed=$SEED,root_owner=0:0"
check edit-uninit
echo "edit-uninit groups: $(dumpe2fs "$WORK/edit-uninit.ext4" 2>/dev/null | grep -c 'BLOCK_UNINIT')"
publish edit-uninit "$S"
