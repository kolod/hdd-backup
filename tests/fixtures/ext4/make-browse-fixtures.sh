#!/bin/bash
# Builds the ext4 fixtures of feature 008 (browse and extract files). Developer tooling only: run
# in Docker (debian:bookworm, e2fsprogs 1.47); the product never uses Linux tools.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" \
#              -v "<repo>/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
#              debian:bookworm bash /w/scripts/make-browse-fixtures.sh
#
# Outputs (in /w/out). Both are bare ext4 images (a single-partition image: no partition table),
# 4 KiB blocks, metadata_csum, 64bit:
#   browse-names.img.gz / .manifest.tsv   names Windows cannot store (a:b, what?.txt, trailing dot and
#                                         space, CON, nul.txt), a name with invalid UTF-8 bytes, a
#                                         case-only pair (Readme / README), UTF-8 names, a path of more
#                                         than 300 characters, fast and slow symlinks, FIFO, socket,
#                                         hard links, an empty file, a text and a binary file
#   browse-large.img.gz / .manifest.tsv   a sparse file with a 2 GiB hole, a 5 MiB file and a
#                                         directory of 20 000 files (those entries are left out of the
#                                         manifest; the test knows the count)
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs attr acl libcap2-bin python3 >/dev/null

OUT=/w/out
WORK=/tmp/browse
rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

export E2FSPROGS_FAKE_TIME=1760000000
FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,64bit,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,metadata_csum"
UUID=11111111-2222-3333-4444-555555555556
SEED=66666666-7777-8888-9999-aaaaaaaaaaab
MIB=$((1024 * 1024))

# shellcheck source=fixture-lib.sh
source "$(dirname "$0")/fixture-lib.sh"

mkfs_dir() { # name stageDir sizeMiB [extra mke2fs args]
    local name=$1 stage=$2 size=$3
    shift 3
    rm -f "$WORK/$name.ext4"
    mke2fs -q -F -t ext4 -b 4096 -I 256 -O none -O "$FEATURES" -L browse \
        -U "$UUID" -E "hash_seed=$SEED,root_owner=0:0" "$@" -d "$stage" "$WORK/$name.ext4" "${size}M"
    e2fsck -fn "$WORK/$name.ext4" >"$WORK/$name-fsck.txt" 2>&1 || { cat "$WORK/$name-fsck.txt"; exit 1; }
}

publish() { # name stageDir [manifest filter regex]
    local name=$1 stage=$2 filter=${3:-}
    if [ -n "$filter" ]; then manifest "$stage" | grep -vE "$filter" > "$OUT/$name.manifest.tsv"
    else manifest "$stage" > "$OUT/$name.manifest.tsv"; fi
    gzip -9 -n -c "$WORK/$name.ext4" > "$OUT/$name.img.gz"
    echo "built $name: $(stat -c %s "$WORK/$name.ext4") bytes, $(wc -l < "$OUT/$name.manifest.tsv") manifest entries"
}

# --- browse-names ------------------------------------------------------------------------
S=$WORK/names-stage
rm -rf "$S"; mkdir -p "$S"/{etc,bin,tricky,deep}
printf 'raspberrypi\n' > "$S/etc/hostname"
content passwd 1500 > "$S/etc/passwd"
# A binary file: text, zero bytes, text.
{ content bin-a 4000; head -c 64 /dev/zero; content bin-b 4000; } > "$S/bin/ls"
chmod 755 "$S/bin/ls"
: > "$S/etc/empty"

content one 100 > "$S/tricky/with space.txt"
content two 120 > "$S/tricky/Ünï cödé.txt"
content three 140 > "$S/tricky/日本語.txt"
content four 160 > "$S/tricky/$(printf 'bad-\xff\xfe-name')"
content five 180 > "$S/tricky/a:b"
content six 200 > "$S/tricky/what?.txt"
content seven 220 > "$S/tricky/trailing."
content eight 240 > "$S/tricky/trailing "
content nine 260 > "$S/tricky/CON"
content ten 280 > "$S/tricky/nul.txt"
content readme-lower 300 > "$S/tricky/Readme"
content readme-upper 320 > "$S/tricky/README"

# A path of more than 300 characters (6 components of 50 characters).
D=$S/deep
for i in 1 2 3 4 5 6; do D=$D/$(printf "d$i%.0s" $(seq 1 24) | head -c 50); done
mkdir -p "$D"
content deep-file 500 > "$D/file.txt"

ln -s ../etc/hostname "$S/tricky/fast-link"
ln -s "/$(printf 'very-long-target-%.0s' $(seq 1 10))/end" "$S/tricky/slow-link"
mkfifo "$S/tricky/fifo"
python3 -c "import socket,sys; s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1])" "$S/tricky/app.sock"
content hard 5000 > "$S/tricky/hard-a"
ln "$S/tricky/hard-a" "$S/tricky/hard-b"
ln "$S/tricky/hard-a" "$S/etc/hard-c"
chown 1000:1000 "$S/tricky/hard-a"
chmod 0640 "$S/etc/passwd"
touch -d '2024-01-02 03:04:05' "$S/etc/hostname"
touch -d '2024-02-03 04:05:06' "$S/tricky/with space.txt"
touch -d '2024-03-04 05:06:07' "$S/etc" "$S/tricky"

mkfs_dir browse-names "$S" 32 -i 16384
publish browse-names "$S"

# --- browse-large ------------------------------------------------------------------------
L=$WORK/large-stage
rm -rf "$L"; mkdir -p "$L"/{data,many}
truncate -s $((2048 * MIB)) "$L/data/hole.bin"
content hole-tail 4096 | dd of="$L/data/hole.bin" bs=4096 seek=$(( 2048 * MIB / 4096 - 1 )) conv=notrunc status=none
content five-mib $((5 * MIB)) > "$L/data/five.bin"
for i in $(seq 1 20000); do : > "$L/many/$(printf 'entry-%05d.dat' "$i")"; done
touch -d '2024-04-05 06:07:08' "$L/data/hole.bin" "$L/data/five.bin" "$L/data" "$L/many"

mkfs_dir browse-large "$L" 128 -N 40000
publish browse-large "$L" '^many/entry-'
