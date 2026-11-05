#!/bin/bash
# Independent check of the diagnostic tools (feature 009, research R-12): what the tools found in the
# fixtures must equal what the real programs say about the same files.
#
#   tests/fixtures/ext4/check-diagnostics.sh <exports-dir> <fixtures-dir> <results.tsv>
#
# The Windows test run exports, per fixture, <exports>/diagnostics/<fixture>.ssh.tsv (keyword TAB value,
# in the form of `sshd -T`) and <fixture>.units.tsv (one enabled unit file name per line). Here the
# fixture is unpacked with debugfs (no mounting), then:
#   * `sshd -T` is run over the fixture's /etc/ssh (Match blocks are not evaluated: no -C), and
#   * `systemctl --root=<tree> list-unit-files --state=enabled` lists the enabled units.
# Appends "diagnostics/<name> TAB OK|FAILED TAB detail" lines to <results.tsv>. Developer/CI tooling only.
set -uo pipefail

EXPORTS=${1:?exports dir}
FIXTURES=${2:?fixtures dir}
RESULTS=${3:?results file}
DIAG=$EXPORTS/diagnostics
[ -d "$DIAG" ] || exit 0

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p /run/sshd 2>/dev/null || true
ssh-keygen -q -t ed25519 -N '' -f "$WORK/hostkey" >/dev/null

KEYWORDS='port|permitrootlogin|passwordauthentication|kbdinteractiveauthentication|usepam|pubkeyauthentication|permitemptypasswords|authorizedkeysfile|allowusers|denyusers|allowgroups|denygroups'

unpack() { # fixture -> dir with the root tree
    local fixture=$1 dir=$WORK/$fixture img fs start size
    mkdir -p "$dir/dump"
    gunzip -c "$FIXTURES/$fixture.img.gz" > "$dir/source.img"
    img=$dir/source.img
    fs=$img
    if sfdisk -d "$img" >/dev/null 2>&1; then
        read -r start size < <(sfdisk -d "$img" | grep -E 'start=' | tail -1 | sed -E 's/.*start= *([0-9]+), size= *([0-9]+).*/\1 \2/')
        dd if="$img" of="$dir/fs.ext4" bs=512 skip="$start" count="$size" status=none
        fs=$dir/fs.ext4
    fi
    debugfs -R "rdump / $dir/dump" "$fs" >/dev/null 2>&1
    echo "$dir/dump"
}

normalize_sshd() { # sshd -T output -> keyword TAB value, sorted; one line per keyword (ports sorted)
    grep -E "^($KEYWORDS) " | sed -E 's/^([a-z]+) /\1\t/' \
        | sed -E 's/\tprohibit-password$/\twithout-password/' \
        | LC_ALL=C sort
}

normalize_tool() {
    sed -E 's/\tprohibit-password$/\twithout-password/' | LC_ALL=C sort
}

fail=0
for ssh in "$DIAG"/*.ssh.tsv; do
    [ -f "$ssh" ] || continue
    fixture=$(basename "$ssh" .ssh.tsv)
    name="diagnostics/$fixture.ssh"
    [ -f "$FIXTURES/$fixture.img.gz" ] || { printf '%s\tMISSING-FIXTURE\n' "$name" >> "$RESULTS"; continue; }
    root=$(unpack "$fixture")
    # Point the absolute Include of the fixture at the unpacked copy.
    cfg=$WORK/$fixture-sshd_config
    sed -e "s#/etc/ssh/#$root/etc/ssh/#g" "$root/etc/ssh/sshd_config" > "$cfg"
    if ! /usr/sbin/sshd -T -f "$cfg" -h "$WORK/hostkey" > "$WORK/$fixture.sshd.out" 2> "$WORK/$fixture.sshd.err"; then
        cat "$WORK/$fixture.sshd.err"
        printf '%s\tFAILED\tsshd -T failed\n' "$name" >> "$RESULTS"
        continue
    fi
    if diff <(normalize_sshd < "$WORK/$fixture.sshd.out") <(normalize_tool < "$ssh") > "$WORK/$fixture.ssh.diff"; then
        printf '%s\tOK\t%s settings identical\n' "$name" "$(wc -l < "$ssh")" >> "$RESULTS"
    else
        echo "--- $name: sshd -T (<) versus the tool (>)"; cat "$WORK/$fixture.ssh.diff"
        printf '%s\tFAILED\n' "$name" >> "$RESULTS"
    fi
done

for units in "$DIAG"/*.units.tsv; do
    [ -f "$units" ] || continue
    fixture=$(basename "$units" .units.tsv)
    name="diagnostics/$fixture.units"
    [ -f "$FIXTURES/$fixture.img.gz" ] || { printf '%s\tMISSING-FIXTURE\n' "$name" >> "$RESULTS"; continue; }
    root=$(unpack "$fixture")
    systemctl --root="$root" list-unit-files --state=enabled --no-legend --no-pager 2> "$WORK/$fixture.sc.err" \
        | awk 'NF { print $1 }' | LC_ALL=C sort -u > "$WORK/$fixture.sc.out"
    if diff "$WORK/$fixture.sc.out" <(LC_ALL=C sort -u "$units") > "$WORK/$fixture.units.diff"; then
        printf '%s\tOK\t%s enabled units identical\n' "$name" "$(grep -c . "$units")" >> "$RESULTS"
    else
        echo "--- $name: systemctl (<) versus the tool (>)"; cat "$WORK/$fixture.units.diff"; cat "$WORK/$fixture.sc.err"
        printf '%s\tFAILED\n' "$name" >> "$RESULTS"
    fi
done
exit 0
