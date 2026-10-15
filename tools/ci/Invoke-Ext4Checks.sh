#!/bin/bash
# Independent e2fsprogs check of every ext4 image exported by the Windows test run
# (HDDBACKUP_EXPORT_DIR). Developer/CI tooling only; the product never uses Linux tools.
#
#   tools/ci/Invoke-Ext4Checks.sh <exports-dir> <fixtures-dir>
#
# Exported names and their source fixture (contracts/scripts.md):
#   <fixture>-<factor>.ext4   bare rebuilt partition (Ext4WriterTests)
#   shrunk-<fixture>.img      full shrunk image (ImageShrinkerTests)
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

if [ ${#jobs[@]} -eq 0 ] && [ ! -s "$LOGS/results.tsv" ]; then
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

printf '%s\n' "${jobs[@]}" | xargs -d '\n' -P "$(nproc)" -I{} bash -c 'check_one "$1"' _ {}

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
