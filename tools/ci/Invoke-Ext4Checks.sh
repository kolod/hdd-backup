#!/bin/bash
# Independent e2fsprogs check of every ext4 image exported by the Windows test run
# (HDDBACKUP_EXPORT_DIR). Developer/CI tooling only; the product never uses Linux tools.
#
#   tools/ci/Invoke-Ext4Checks.sh <exports-dir> <fixtures-dir>
#
# Exported names and their source fixture (contracts/scripts.md):
#   <fixture>-<factor>.ext4   bare rebuilt partition (Ext4WriterTests)
#   shrunk-<fixture>.img      full shrunk image (ImageShrinkerTests)
#   repaired-<fixture>.img    repaired image (RepairTests), compared with <fixture>.reference.img.gz
# Exit 1 when no image is found or any image fails. Images are checked in parallel.
set -uo pipefail

EXPORTS=${1:?exports dir}
FIXTURES=${2:?fixtures dir}
CHECK="$(cd "$(dirname "$0")/../.." && pwd)/tests/fixtures/ext4/check-output.sh"
LOGS=$(mktemp -d)
SUMMARY=${GITHUB_STEP_SUMMARY:-/dev/stdout}

source_of() { # exported file name -> fixture name, or empty
    local name=$1
    if [[ $name =~ ^shrunk-(.+)\.img$ ]]; then echo "${BASH_REMATCH[1]}"; return; fi
    if [[ $name =~ ^repaired-(.+)\.img$ ]]; then echo "${BASH_REMATCH[1]}.reference"; return; fi
    if [[ $name =~ ^(.+)-[0-9]+\.[0-9]{2}\.ext4$ ]]; then echo "${BASH_REMATCH[1]}"; return; fi
}

jobs=()
for path in "$EXPORTS"/*; do
    [ -f "$path" ] || continue
    name=$(basename "$path")
    fixture=$(source_of "$name")
    if [ -z "$fixture" ]; then
        echo "skip (unknown name): $name"
        continue
    fi
    if [ ! -f "$FIXTURES/$fixture.img.gz" ]; then
        echo "::error::No source fixture $fixture.img.gz for $name"
        echo "$name	MISSING-FIXTURE" >> "$LOGS/results.tsv"
        continue
    fi
    jobs+=("$path"$'\t'"$FIXTURES/$fixture.img.gz")
done

if [ ${#jobs[@]} -eq 0 ] && [ ! -s "$LOGS/results.tsv" ] && ! compgen -G "$EXPORTS/extracted/*.tsv" > /dev/null; then
    echo "::error::No exported ext4 images found in $EXPORTS"
    exit 1
fi

# shellcheck disable=SC2317  # called through xargs below
check_one() { # "<image>\t<source>"
    local image=${1%%$'\t'*} source=${1#*$'\t'} name log
    name=$(basename "$image")
    log="$LOGS/$name.log"
    if bash "$CHECK" "$image" "$source" > "$log" 2>&1; then
        echo "$name	OK	$(grep -Eo '[0-9]+ objects identical' "$log" | head -1)" >> "$LOGS/results.tsv"
    else
        echo "$name	FAILED" >> "$LOGS/results.tsv"
    fi
}
export -f check_one
export CHECK LOGS

[ ${#jobs[@]} -eq 0 ] || printf '%s\n' "${jobs[@]}" | xargs -d '\n' -P "$(nproc)" -I{} bash -c 'check_one "$1"' _ {}

# Trees extracted by the Files tab tests (ExtractorTests): <exports>/extracted/<fixture>.tsv lists the
# directories ("D<TAB>path") and regular files ("F<TAB>sha256<TAB>path") they extracted. It must equal
# `debugfs rdump` of the fixture (symbolic links, FIFOs, sockets and device files are not extracted
# on Windows and are not compared). Plain bytes, so no character set conversion is involved.
tree_listing() { # dir -> the same listing format, sorted bytewise
    (
        cd "$1" || exit 1
        find . -mindepth 1 -type d | sed -e 's|^\./||' -e 's|^|D\t|'
        find . -type f -exec sha256sum {} + | sed -E 's|^([0-9a-f]{64}) [ *]\./(.*)$|F\t\1\t\2|'
    ) | LC_ALL=C sort
}

check_extracted() { # fixture-name listing.tsv
    local fixture=$1 listing=$2 work img fs start size
    local gz="$FIXTURES/$fixture.img.gz"
    [ -f "$gz" ] || { echo "extracted/$fixture	MISSING-FIXTURE" >> "$LOGS/results.tsv"; return; }
    work=$(mktemp -d)
    gunzip -c "$gz" > "$work/source.img"
    img=$work/source.img
    if sfdisk -d "$img" >/dev/null 2>&1; then
        read -r start size < <(sfdisk -d "$img" | grep -E 'start=' | tail -1 | sed -E 's/.*start= *([0-9]+), size= *([0-9]+).*/\1 \2/')
        dd if="$img" of="$work/fs.ext4" bs=512 skip="$start" count="$size" status=none
        fs=$work/fs.ext4
    else
        fs=$img
    fi
    mkdir "$work/dump"
    debugfs -R "rdump / $work/dump" "$fs" >/dev/null 2>&1
    mkdir -p "$LOGS/extracted"
    if diff <(tree_listing "$work/dump") <(LC_ALL=C sort "$listing") > "$LOGS/extracted/$fixture.log" 2>&1; then
        echo "extracted/$fixture	OK	$(grep -c '^F' "$listing") files identical" >> "$LOGS/results.tsv"
    else
        echo "extracted/$fixture	FAILED" >> "$LOGS/results.tsv"
    fi
    rm -rf "$work"
}

for listing in "$EXPORTS"/extracted/*.tsv; do
    [ -f "$listing" ] || continue
    check_extracted "$(basename "$listing" .tsv)" "$listing"
done

failed=0
{
    echo "### Independent ext4 check (e2fsprogs $(e2fsck -V 2>&1 | head -1 | awk '{print $2}'))"
    echo
    echo "| Image | Result |"
    echo "|-------|--------|"
    sort "$LOGS/results.tsv" | while IFS=$'\t' read -r name result detail; do
        echo "| $name | $result ${detail:-} |"
    done
} >> "$SUMMARY"

while IFS=$'\t' read -r name result _; do
    if [ "$result" != "OK" ]; then
        failed=1
        echo "::error::ext4 check failed for $name"
        [ -f "$LOGS/$name.log" ] && sed 's/^/    /' "$LOGS/$name.log" | tail -40
    else
        echo "OK  $name"
    fi
done < <(sort "$LOGS/results.tsv")

exit $failed
