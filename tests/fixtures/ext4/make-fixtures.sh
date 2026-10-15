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

# Deterministic, compressible but position-unique content: numbered text lines.
content() { # name bytes
    seq -f "$1 line %010g" 1 $(( $2 / 30 + 1 )) | head -c "$2"
}

stage_root() { # dir
    local d=$1
    mkdir -p "$d"/{etc,usr/bin,usr/lib/big,var/log,home/pi,dev-nodes,empty-dir}
    content passwd 1500 > "$d/etc/passwd"
    content fstab 300 > "$d/etc/fstab"
    content bash 900000 > "$d/usr/bin/bash"
    content libc 1800000 > "$d/usr/lib/libc.so.6"
    for i in $(seq 1 40); do content "lib$i" $(( (i * 37 % 200 + 1) * 1024 )) > "$d/usr/lib/lib$i.so"; done
    # Large directory (more than one block of entries).
    for i in $(seq 1 1500); do printf 'f%04d\n' "$i" > "$d/usr/lib/big/entry-with-a-longer-name-$i.conf"; done
    # Long names (255 bytes) and UTF-8.
    touch "$d/home/pi/$(printf 'n%.0s' $(seq 1 255))"
    content utf8 100 > "$d/home/pi/файл-日本語.txt"
    # Sparse file: 8 MiB with data only at both ends.
    truncate -s $((8 * MIB)) "$d/var/log/sparse.img"
    content sparse-head 4096 | dd of="$d/var/log/sparse.img" conv=notrunc status=none
    content sparse-tail 4096 | dd of="$d/var/log/sparse.img" bs=4096 seek=$(( 8 * MIB / 4096 - 1 )) conv=notrunc status=none
    # Hard links (three names, one inode), symlinks (fast and slow).
    content hard 5000 > "$d/home/pi/hard-a"
    ln "$d/home/pi/hard-a" "$d/home/pi/hard-b"
    ln "$d/home/pi/hard-a" "$d/etc/hard-c"
    ln -s ../etc/passwd "$d/home/pi/fast-link"
    ln -s "/$(printf 'very-long-target-%.0s' $(seq 1 10))/end" "$d/home/pi/slow-link"
    # Device nodes, FIFO.
    mknod "$d/dev-nodes/null" c 1 3
    mknod "$d/dev-nodes/sda" b 8 0
    mkfifo "$d/dev-nodes/fifo"
    # Ownership, modes, extended attributes, ACL, capability.
    chown 1000:1000 "$d/home/pi" "$d/home/pi/hard-a"
    chmod 4755 "$d/usr/bin/bash"
    chmod 0600 "$d/etc/fstab"
    setfattr -n user.comment -v "hello xattr" "$d/etc/passwd"
    # Larger than the in-inode space, so it goes to an external xattr block (must fit one block).
    setfattr -n user.big -v "$(head -c "${BIG_XATTR:-3000}" /dev/zero | tr '\0' 'x')" "$d/etc/fstab"
    setfacl -m u:1000:rw "$d/etc/passwd"
    setcap cap_net_raw+ep "$d/usr/bin/bash"
    touch -d '2024-01-02 03:04:05.123456789' "$d/etc/passwd"
}

manifest() { # dir -> TSV on stdout (path type mode uid gid size mtime sha256 link rdev hardlink-key xattrs)
    local d=$1
    (cd "$d" && find . -mindepth 1 -printf '%P\n' | LC_ALL=C sort) | while IFS= read -r p; do
        local f="$d/$p"
        local type sha=- link=- rdev=- key=- xattrs
        type=$(stat -c %F "$f")
        case "$type" in
            "regular file"|"regular empty file") type=file; sha=$(sha256sum "$f" | cut -d' ' -f1) ;;
            directory) type=dir ;;
            "symbolic link") type=symlink; link=$(readlink "$f") ;;
            "character special file") type=chr; rdev=$(stat -c '%t:%T' "$f") ;;
            "block special file") type=blk; rdev=$(stat -c '%t:%T' "$f") ;;
            fifo) type=fifo ;;
            socket) type=sock ;;
        esac
        [ "$(stat -c %h "$f")" -gt 1 ] && [ "$type" != dir ] && key="hl$(stat -c %i "$f")"
        xattrs=$(getfattr -h -d -m - -e hex "$f" 2>/dev/null | grep '=' | LC_ALL=C sort | tr '\n' ';' || true)
        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
            "$p" "$type" "$(stat -c %a "$f")" "$(stat -c %u "$f")" "$(stat -c %g "$f")" \
            "$(stat -c %s "$f")" "$(stat -c %Y "$f")" "$sha" "$link" "$rdev" "$key" "${xattrs:--}"
    done
}

make_boot() { # file MiB [fat bits]
    local f=$1 size=$2 bits=${3:-32}
    rm -f "$f"
    mkfs.vfat -F "$bits" -n BOOT -C "$f" $(( size * 1024 )) >/dev/null
    content config 2000 > /tmp/config.txt
    content cmdline 200 > /tmp/cmdline.txt
    mcopy -i "$f" /tmp/config.txt /tmp/cmdline.txt ::/
}

assemble() { # out boot.vfat root.ext4 [extra.part]; MBR (or GPT if LABEL=gpt), 1 MiB aligned
    local out=$1 boot=$2 root=$3 extra=${4:-}
    local b=$(( $(stat -c %s "$boot") / 512 )) r=$(( $(stat -c %s "$root") / 512 ))
    local s1=2048 s2=$(( 2048 + b )) s3=0 total
    total=$(( s2 + r ))
    if [ -n "$extra" ]; then s3=$total; total=$(( s3 + $(stat -c %s "$extra") / 512 )); fi
    [ "${LABEL:-dos}" = gpt ] && total=$(( total + 2048 ))   # room for the backup GPT
    rm -f "$out"; truncate -s $(( total * 512 )) "$out"
    if [ "${LABEL:-dos}" = gpt ]; then
        {
            echo "label: gpt"
            echo "label-id: 12345678-ABCD-4EF0-9876-0123456789AB"
            echo "start=$s1, size=$b, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7, name=boot"
            echo "start=$s2, size=$r, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name=rootfs"
        } | sfdisk -q "$out"
    else
    {
        echo "label: dos"
        echo "label-id: 0x1234abcd"
        echo "start=$s1, size=$b, type=c"
        echo "start=$s2, size=$r, type=83"
        if [ -n "$extra" ]; then echo "start=$s3, size=$(( $(stat -c %s "$extra") / 512 )), type=c"; fi
    } | sfdisk -q "$out"
    fi
    dd if="$boot" of="$out" bs=512 seek=$s1 conv=notrunc status=none
    dd if="$root" of="$out" bs=512 seek=$s2 conv=notrunc status=none
    [ -n "$extra" ] && dd if="$extra" of="$out" bs=512 seek=$s3 conv=notrunc status=none
    true
}

# Fragmentation and data near the end: fill with fillers, add a tail file, punch holes by
# removing every other filler, then write a file that must span several holes.
fragment() { # root.ext4 staging-final fillers sizeKiB
    local img=$1 final=$2 n=$3 kib=$4
    for i in $(seq 1 "$n"); do content "filler$i" $(( kib * 1024 )) > "/tmp/filler$i"; done
    content tail $(( 3 * 1024 * 1024 )) > /tmp/tail.bin
    content frag $(( kib * 1024 * 5 / 2 )) > /tmp/frag.bin
    {
        echo "mkdir /fill"
        for i in $(seq 1 "$n"); do echo "write /tmp/filler$i /fill/filler$i"; done
        echo "write /tmp/tail.bin /tail.bin"
        for i in $(seq 1 2 "$n"); do echo "rm /fill/filler$i"; done
        echo "write /tmp/frag.bin /frag.bin"
        echo "sif /tail.bin mtime 1700000000"
        echo "sif /frag.bin mtime 1700000001"
        echo "sif /fill mtime 1700000002"
        for i in $(seq 2 2 "$n"); do echo "sif /fill/filler$i mtime 1700000003"; done
    } > /tmp/frag.cmd
    debugfs -w -f /tmp/frag.cmd "$img" >/dev/null 2>&1
    mkdir -p "$final/fill"
    for i in $(seq 2 2 "$n"); do cp /tmp/filler$i "$final/fill/filler$i"; chmod 644 "$final/fill/filler$i"; touch -d @1700000003 "$final/fill/filler$i"; done
    cp /tmp/tail.bin "$final/tail.bin"; chmod 644 "$final/tail.bin"; touch -d @1700000000 "$final/tail.bin"
    cp /tmp/frag.bin "$final/frag.bin"; chmod 644 "$final/frag.bin"; touch -d @1700000001 "$final/frag.bin"
    chmod 755 "$final/fill"; touch -d @1700000002 "$final/fill"
}

build_rpi_like() { # name blockSize rootMiB fillers fillerKiB [features]
    local name=$1 bs=$2 rootmib=$3 n=$4 kib=$5 features=${6:-$FEATURES}
    local stage=$WORK/$name-stage
    rm -rf "$stage"; stage_root "$stage"
    make_boot "$WORK/$name-boot.vfat" 64 32
    rm -f "$WORK/$name-root.ext4"
    mke2fs -q -F -t ext4 -b "$bs" -I 256 -i 16384 -O "none" -O "$features" -L rootfs \
        -U "$UUID" -E "hash_seed=$SEED,root_owner=0:0" -d "$stage" "$WORK/$name-root.ext4" "${rootmib}M"
    fragment "$WORK/$name-root.ext4" "$stage" "$n" "$kib"
    e2fsck -fn "$WORK/$name-root.ext4" > "$WORK/$name-fsck.txt" 2>&1 || { cat "$WORK/$name-fsck.txt"; exit 1; }
    assemble "$WORK/$name.img" "$WORK/$name-boot.vfat" "$WORK/$name-root.ext4"
    manifest "$stage" > "$OUT/$name.manifest.tsv"
    gzip -9 -c "$WORK/$name.img" > "$OUT/$name.img.gz"
    echo "built $name: $(stat -c %s "$WORK/$name.img") bytes, $(wc -l < "$OUT/$name.manifest.tsv") entries"
}

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
