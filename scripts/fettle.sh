#!/usr/bin/env bash
# Fettler against THIS repository, with the roots already declared.
#
#   scripts/fettle.sh find "fettler/**/*.cs"
#   scripts/fettle.sh search "^public sealed" --glob "fettler/**/*.cs" --json
#   scripts/fettle.sh roots
#
# The twin of fettle.ps1 and equivalent to it. This is what R3.3 is for:
# the logic lives in the program, so neither file holds any and they
# cannot drift from each other.
#
# The roots are ABSOLUTE, computed from this script's own location rather
# than from wherever it was invoked - a relative root would bind the
# boundary to the caller's current directory.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(dirname "$here")
notes=$(dirname "$repo")/shoddy-planning

# Whichever fettle this checkout has, in the order a maintainer wants
# it, with a refusal that says what it looked for.
exe=""
for candidate in \
    "$repo/artifacts/publish/fettle/linux-x64/fettle" \
    "$repo/artifacts/bin/fettle/release/fettle" \
    "$repo/artifacts/bin/fettle/debug/fettle"; do
    [ -x "$candidate" ] && { exe="$candidate"; break; }
done
if [ -z "$exe" ]; then
    command -v fettle >/dev/null 2>&1 && exe=fettle || {
        echo "STOPPED: no fettle found. Build it with fettler/build.sh, or put it on PATH." >&2
        exit 1
    }
fi


roots=(--root "repo=$repo")
[ -d "$notes" ] && roots+=(--root "notes=$notes")

exec "$exe" "${roots[@]}" "$@"
