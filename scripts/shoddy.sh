#!/usr/bin/env sh
# MAINTAINER TOOL - the release, from a POSIX shell.
#
#   scripts/shoddy.sh doctor
#   scripts/shoddy.sh preflight [2.0.3]
#   scripts/shoddy.sh gate [--resume]
#   scripts/shoddy.sh package 2.0.3
#   scripts/shoddy.sh publish 2.0.3 --yes
#   scripts/shoddy.sh release 2.0.3
#   scripts/shoddy.sh status
#   scripts/shoddy.sh clean
#
# DELIBERATELY THIN - see the note in shoddy.ps1. The logic lives once, in
# scripts/gate/driver.mjs, so the two shells cannot drift apart.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
driver="$here/gate/driver.mjs"

if ! command -v node >/dev/null 2>&1; then
    echo "STOPPED: node is not on PATH, and the release gates are Node." >&2
    exit 1
fi

exec node "$driver" "$@"
