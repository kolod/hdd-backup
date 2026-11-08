#!/bin/bash
# Independent check of the file editing feature (feature 010): every image the tests saved after editing must
# be a clean ext4 file system for the real e2fsck, and its tree must equal the tree the test expected.
#
#   tests/fixtures/ext4/check-editing.sh <exports-dir> <fixtures-dir> <results.tsv>
#
# The Windows test run exports, per scenario, <exports>/editing/saved-<scenario>.img (the whole image, a bare
# ext4 file system or one with a partition table; the last partition is checked) and
# saved-<scenario>.tree.tsv (D<TAB>path and F<TAB>sha256<TAB>path, the format of tools/ci/Invoke-Ext4Checks.sh).
# Appends "editing/<scenario> TAB OK|FAILED TAB detail" lines to <results.tsv>. Developer/CI tooling only.
set -uo pipefail

EXPORTS=${1:?exports dir}
RESULTS=${3:?results file}
DIR=$EXPORTS/editing
[ -d "$DIR" ] || exit 0

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

tree_listing() { # dir -> D/F listing sorted bytewise
    (
        cd "$1" || exit 1
        find . -mindepth 1 -type d | sed -e 's|^\./||' -e 's|^|D\t|'
        find . -type f -exec sha256sum {} + | sed -E 's|^([0-9a-f]{64}) [ *]\./(.*)$|F\t\1\t\2|'
    ) | LC_ALL=C sort
}

for img in "$DIR"/saved-*.img; do
    [ -f "$img" ] || continue
    name=$(basename "$img" .img)
    scenario=${name#saved-}
    fs=$img
    if sfdisk -d "$img" >/dev/null 2>&1; then
        read -r start size < <(sfdisk -d "$img" | grep -E 'start=' | tail -1 | sed -E 's/.*start= *([0-9]+), size= *([0-9]+).*/\1 \2/')
        dd if="$img" of="$WORK/$scenario.fs" bs=512 skip="$start" count="$size" status=none
        fs=$WORK/$scenario.fs
    fi
    if ! e2fsck -fn "$fs" > "$WORK/$scenario.fsck" 2>&1; then
        sed 's/^/    /' "$WORK/$scenario.fsck" | tail -30
        printf 'editing/%s\tFAILED\te2fsck reported problems\n' "$scenario" >> "$RESULTS"
        continue
    fi
    attrs=$DIR/$name.attrs.tsv
    if [ -f "$attrs" ] && [[ $scenario == ssh-* ]]; then
        # feature 011: modes, owners and link targets of every entry, from the real debugfs
        cut -f1 "$attrs" | sed 's|^|stat |' > "$WORK/$scenario.cmds"
        debugfs -f "$WORK/$scenario.cmds" "$fs" 2>/dev/null | awk '
            function flush() {
                if (path != "") { printf "%s\t%s\t%s\t%s\t%s\n", path, mode, uid, gid, (link == "" ? "-" : link) }
                path = ""; link = ""; mode = ""; uid = ""; gid = ""
            }
            /^debugfs: stat / { flush(); path = substr($0, 15) }
            /Mode: / { for (i = 1; i <= NF; i++) { if ($i == "Mode:") { mode = $(i + 1) } } sub(/^0+/, "", mode) }
            /^User:/ { uid = $2; gid = $4 }
            /Fast link dest:/ { match($0, /"[^"]*"/); link = substr($0, RSTART + 1, RLENGTH - 2) }
            END { flush() }' | LC_ALL=C sort > "$WORK/$scenario.attrs.actual"
        LC_ALL=C sort "$attrs" | awk -F'\t' '{ m = $2; sub(/^0+/, "", m); printf "%s\t%s\t%s\t%s\t%s\n", $1, m, $3, $4, $5 }' > "$WORK/$scenario.attrs.expected"
        if ! diff "$WORK/$scenario.attrs.expected" "$WORK/$scenario.attrs.actual" > "$WORK/$scenario.attrs.diff" 2>&1; then
            echo "--- editing/$scenario: modes/owners/links differ (<: expected, >: image)"; head -20 "$WORK/$scenario.attrs.diff"
            printf 'editing/%s\tFAILED\tmodes, owners or links differ\n' "$scenario" >> "$RESULTS"
            continue
        fi
    fi
    expected=$DIR/$name.tree.tsv
    if [ -f "$expected" ]; then
        rm -rf "$WORK/$scenario.dump"; mkdir "$WORK/$scenario.dump"
        debugfs -R "rdump / $WORK/$scenario.dump" "$fs" >/dev/null 2>&1
        if diff <(tree_listing "$WORK/$scenario.dump") <(LC_ALL=C sort "$expected") > "$WORK/$scenario.diff" 2>&1; then
            printf 'editing/%s\tOK\te2fsck clean, %s files identical\n' "$scenario" "$(grep -c '^F' "$expected")" >> "$RESULTS"
        else
            echo "--- editing/$scenario: tree differs (<: image, >: expected)"; head -20 "$WORK/$scenario.diff"
            printf 'editing/%s\tFAILED\ttree differs\n' "$scenario" >> "$RESULTS"
        fi
    else
        printf 'editing/%s\tOK\te2fsck clean\n' "$scenario" >> "$RESULTS"
    fi
done
exit 0
