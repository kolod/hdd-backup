#!/bin/bash
# Builds ext4 fixtures with the damage an unclean shutdown leaves (feature 006), plus, for each,
# the Linux reference: the same image after `e2fsck -fy`. Developer tooling only: run in Docker
# (debian:bookworm, e2fsprogs 1.47); the product never uses Linux tools.
#
#   docker run --rm -v "<repo>/tests/fixtures/ext4:/w/scripts:ro" \
#              -v "<repo>/tests/HddBackup.Core.Tests/Fixtures/ext4:/w/out" \
#              debian:bookworm bash /w/scripts/make-repair-fixtures.sh
#
# Outputs (in /w/out), MBR: FAT16 boot + ext4 root (4 KiB blocks, metadata_csum, 64bit):
#   dirty-journal.img.gz        changes exist only in the journal (3 transactions, csum v3)
#   dirty-journal-torn.img.gz   same idea, the last of 2 transactions has no commit block
#   orphans.img.gz              2 deleted-while-open inodes + 1 truncate in progress on the orphan list
#   not-clean.img.gz            clean flag cleared, wrong free counts, a used block marked free
#   damaged-dirent.img.gz       a directory entry points to a cleared inode (structural; no reference)
#   <name>.reference.img.gz     the image after `e2fsck -fy` (for the first four)
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq e2fsprogs dosfstools mtools fdisk attr acl libcap2-bin python3-minimal >/dev/null

OUT=/w/out
WORK=/tmp/repair
rm -rf "$WORK" && mkdir -p "$WORK" "$OUT"
cd "$WORK"

export E2FSPROGS_FAKE_TIME=1760000000
FEATURES="has_journal,ext_attr,resize_inode,dir_index,filetype,extent,64bit,flex_bg,sparse_super,large_file,huge_file,dir_nlink,extra_isize,metadata_csum"
UUID=11111111-2222-3333-4444-555555555555
SEED=66666666-7777-8888-9999-aaaaaaaaaaaa
MIB=$((1024 * 1024))
BS=4096

# shellcheck source=fixture-lib.sh
source "$(dirname "$0")/fixture-lib.sh"

make_boot "$WORK/boot.vfat" 16 16

base_root() { # out.ext4
    rm -rf "$WORK/stage"; stage_root "$WORK/stage"
    rm -f "$1"
    mke2fs -q -F -t ext4 -b $BS -I 256 -i 16384 -O none -O "$FEATURES" -L rootfs \
        -U "$UUID" -E "hash_seed=$SEED,root_owner=0:0" -d "$WORK/stage" "$1" 64M
}

# Blocks that differ between two file systems, split into N transactions: <prefix>.<i>.blocks / .bin
diff_chunks() { # a.ext4 b.ext4 n prefix
    python3 - "$@" <<'PY'
import sys
a, b, n, prefix = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
bs = 4096
A = open(a, 'rb').read(); B = open(b, 'rb').read()
diff = [i for i in range(len(A) // bs) if A[i*bs:(i+1)*bs] != B[i*bs:(i+1)*bs]]
assert diff, 'no differences'
size = (len(diff) + n - 1) // n
for k in range(n):
    part = diff[k*size:(k+1)*size]
    open(f'{prefix}.{k}.blocks', 'w').write(','.join(map(str, part)))
    open(f'{prefix}.{k}.bin', 'wb').write(b''.join(B[i*bs:(i+1)*bs] for i in part))
print(f'{len(diff)} blocks in {n} transactions')
PY
}

# Writes the transactions of diff_chunks into the journal of a.ext4 (checksummed, csum v3).
journal_write() { # a.ext4 prefix n
    local img=$1 prefix=$2 n=$3
    {
        echo "jo -c"
        for k in $(seq 0 $(( n - 1 ))); do echo "jw -b $(cat "$prefix.$k.blocks") $prefix.$k.bin"; done
        echo "jc"
    } > "$WORK/jw.cmd"
    debugfs -w -f "$WORK/jw.cmd" "$img" >/dev/null 2>&1
    dumpe2fs -h "$img" 2>/dev/null | grep -q needs_recovery || { echo "needs_recovery not set"; exit 1; }
}

# Changes made "while the device was running".
changes_1() { # img
    content newlog 300000 > /tmp/newlog
    content lib7v2 70000 > /tmp/lib7
    debugfs -w -f - "$1" >/dev/null 2>&1 <<CMDS
write /tmp/newlog /home/pi/new-after-boot.log
sif /home/pi/new-after-boot.log mtime 1700001000
rm /usr/lib/lib7.so
write /tmp/lib7 /usr/lib/lib7.so
sif /usr/lib/lib7.so mtime 1700001001
rm /usr/lib/lib3.so
sif /home/pi mtime 1700001002
sif /usr/lib mtime 1700001003
CMDS
}

changes_2() { # img
    content journal2 120000 > /tmp/j2
    debugfs -w -f - "$1" >/dev/null 2>&1 <<CMDS
mkdir /var/log/apt
write /tmp/j2 /var/log/apt/history.log
sif /var/log/apt/history.log mtime 1700002000
sif /var/log/apt mtime 1700002001
sif /var/log mtime 1700002002
rm /usr/lib/lib11.so
sif /usr/lib mtime 1700002003
CMDS
}

finish() { # name root.ext4 [noref]
    local name=$1 root=$2
    assemble "$WORK/$name.img" "$WORK/boot.vfat" "$root"
    gzip -9 -n -c "$WORK/$name.img" > "$OUT/$name.img.gz"
    if [ -z "${3:-}" ]; then
        cp "$root" "$WORK/$name-ref.ext4"
        e2fsck -fy "$WORK/$name-ref.ext4" > "$WORK/$name-ref.txt" 2>&1 || true
        e2fsck -fn "$WORK/$name-ref.ext4" > /dev/null 2>&1 || { echo "$name: reference not clean"; cat "$WORK/$name-ref.txt"; exit 1; }
        assemble "$WORK/$name.reference.img" "$WORK/boot.vfat" "$WORK/$name-ref.ext4"
        gzip -9 -n -c "$WORK/$name.reference.img" > "$OUT/$name.reference.img.gz"
        echo "== $name: e2fsck -fy said:"; grep -v '^e2fsck\|^Pass\|^$' "$WORK/$name-ref.txt" | head -12
    fi
}

# 1. Journal with three complete transactions; the on-disk blocks are the old ones.
base_root "$WORK/a.ext4"
cp "$WORK/a.ext4" "$WORK/b.ext4"
changes_1 "$WORK/b.ext4"; changes_2 "$WORK/b.ext4"
diff_chunks "$WORK/a.ext4" "$WORK/b.ext4" 3 "$WORK/dj"
cp "$WORK/a.ext4" "$WORK/dirty-journal.ext4"
journal_write "$WORK/dirty-journal.ext4" "$WORK/dj" 3
finish dirty-journal "$WORK/dirty-journal.ext4"
# The recovered tree must equal the file system the changes were made on.
mkdir -p "$WORK/t1" "$WORK/t2"
debugfs -R "rdump / $WORK/t1" "$WORK/b.ext4" >/dev/null 2>&1
debugfs -R "rdump / $WORK/t2" "$WORK/dirty-journal-ref.ext4" >/dev/null 2>&1
diff -r "$WORK/t1" "$WORK/t2" && echo "dirty-journal: recovered tree equals the changed file system"

# 2. Torn: transaction 1 = changes_1, transaction 2 = changes_2 without its commit block.
cp "$WORK/a.ext4" "$WORK/b1.ext4"; changes_1 "$WORK/b1.ext4"
cp "$WORK/b1.ext4" "$WORK/b2.ext4"; changes_2 "$WORK/b2.ext4"
diff_chunks "$WORK/a.ext4" "$WORK/b1.ext4" 1 "$WORK/t1x"
diff_chunks "$WORK/b1.ext4" "$WORK/b2.ext4" 1 "$WORK/t2x"
cp "$WORK/t2x.0.blocks" "$WORK/tt.1.blocks"; cp "$WORK/t2x.0.bin" "$WORK/tt.1.bin"
cp "$WORK/t1x.0.blocks" "$WORK/tt.0.blocks"; cp "$WORK/t1x.0.bin" "$WORK/tt.0.bin"
cp "$WORK/a.ext4" "$WORK/torn.ext4"
journal_write "$WORK/torn.ext4" "$WORK/tt" 2
commit=$(debugfs -R logdump "$WORK/torn.ext4" 2>/dev/null | grep 'commit block' | tail -1 | sed -E 's/.* at block ([0-9]+).*/\1/')
phys=$(debugfs -R "bmap <8> $commit" "$WORK/torn.ext4" 2>/dev/null)
dd if=/dev/zero of="$WORK/torn.ext4" bs=$BS seek="$phys" count=1 conv=notrunc status=none
echo "torn: zeroed journal block $commit (fs block $phys)"
finish dirty-journal-torn "$WORK/torn.ext4"
rm -rf "$WORK/t1" "$WORK/t2"; mkdir -p "$WORK/t1" "$WORK/t2"
debugfs -R "rdump / $WORK/t1" "$WORK/b1.ext4" >/dev/null 2>&1
debugfs -R "rdump / $WORK/t2" "$WORK/dirty-journal-torn-ref.ext4" >/dev/null 2>&1
diff -r "$WORK/t1" "$WORK/t2" && echo "dirty-journal-torn: recovered tree equals transaction 1 only"

# 3. Orphans: two deleted-while-open inodes and one unfinished truncate.
base_root "$WORK/orphans.ext4"
content orphan1 200000 > /tmp/o1; content orphan2 50000 > /tmp/o2; content trunc 3000000 > /tmp/tr
debugfs -w -f - "$WORK/orphans.ext4" >/dev/null 2>&1 <<CMDS
write /tmp/o1 /var/log/o1
write /tmp/o2 /var/log/o2
write /tmp/tr /var/log/trunc.bin
sif /var/log/trunc.bin mtime 1700003000
sif /var/log mtime 1700003001
CMDS
o1=$(debugfs -R "stat /var/log/o1" "$WORK/orphans.ext4" 2>/dev/null | sed -nE 's/^Inode: ([0-9]+).*/\1/p')
o2=$(debugfs -R "stat /var/log/o2" "$WORK/orphans.ext4" 2>/dev/null | sed -nE 's/^Inode: ([0-9]+).*/\1/p')
tr=$(debugfs -R "stat /var/log/trunc.bin" "$WORK/orphans.ext4" 2>/dev/null | sed -nE 's/^Inode: ([0-9]+).*/\1/p')
debugfs -w -f - "$WORK/orphans.ext4" >/dev/null 2>&1 <<CMDS
unlink /var/log/o1
unlink /var/log/o2
sif <$o1> links_count 0
sif <$o2> links_count 0
sif <$o1> dtime $o2
sif <$o2> dtime $tr
sif <$tr> dtime 0
sif <$tr> size 1000000
ssv last_orphan $o1
CMDS
echo "orphans: chain $o1 -> $o2 -> $tr (truncate to 1000000)"
finish orphans "$WORK/orphans.ext4"

# 4. Not cleanly unmounted, stale free counts, one used block marked free.
base_root "$WORK/notclean.ext4"
blk=$(debugfs -R "bmap /usr/lib/libc.so.6 3" "$WORK/notclean.ext4" 2>/dev/null)
debugfs -w -f - "$WORK/notclean.ext4" >/dev/null 2>&1 <<CMDS
ssv state 0
ssv free_blocks_count 1000
ssv free_inodes_count 1000
set_bg 0 free_blocks_count 7
set_bg 0 checksum calc
freeb $blk
CMDS
echo "not-clean: freed used block $blk"
finish not-clean "$WORK/notclean.ext4"

# 5. Structural damage: a directory entry points to a cleared inode.
base_root "$WORK/damaged.ext4"
debugfs -w -R "clri /etc/fstab" "$WORK/damaged.ext4" >/dev/null 2>&1
finish damaged-dirent "$WORK/damaged.ext4" noref

ls -l "$OUT"/dirty-journal* "$OUT"/orphans* "$OUT"/not-clean* "$OUT"/damaged-dirent*
