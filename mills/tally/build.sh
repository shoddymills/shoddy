#!/usr/bin/env bash
# Build / run the tally mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh run [SPEC]       read the spec, print the report, show the chart
#   ./build.sh capture [SPEC]   the same, with nothing on screen — the PNG is the output
#   ./build.sh test             the headless suite: no window, no display
#   ./build.sh build            weave the program into bin/
#   ./build.sh clean            remove bin/
#
# run and capture are LIVE: they launch from wherever you actually typed
# ./build.sh, not from this directory. SPEC defaults to this mill's own
# files/grades.spec if you don't give one; a SPEC you do name resolves
# against your own shell, same as any ordinary file argument would.
# Paths INSIDE a spec (data.file, window.capture) are relative to
# wherever the spec itself resolved from — the shipped default spec
# names files/... to match sitting beside it.
#
# capture passes --no-window, which opens every scribbler hidden and stops
# windows outliving the program. Pair it with window.show = no in the spec:
# the flag says "put nothing on screen", the spec key says "do not wait for
# anyone to dismiss it". A spec that shows, run with --no-window, would
# otherwise be waiting on a window nobody can see.
#
# The test target needs NOTHING — no display, no network. Everything
# between a file's text and a finished report is pure, which is the whole
# reason tally-core.shoddy and tally.shoddy are separate files.
set -euo pipefail
ORIG_DIR=$(pwd)
cd "$(dirname "$0")"
. ../../scripts/mill-common.sh

SPEC=${2:-$MILL_DIR/files/grades.spec}

case "${1:-run}" in
    run)
        ensure_mill
        run_live "$MILL" run "$MILL_DIR/tally.shoddy" "$SPEC"
        ;;
    capture)
        ensure_mill
        run_live "$MILL" run --no-window "$MILL_DIR/tally.shoddy" "$SPEC"
        ;;
    test)
        # test.shoddy's own fixtureDir is repo-root-relative
        # (mills/tally/files/), not mill-relative, so this one runs from
        # the repo root rather than staying pinned to this directory.
        ensure_mill
        run_from_repo "$MILL" run mills/tally/test.shoddy < /dev/null
        ;;
    build)
        weave_and_move tally.shoddy
        echo "woven into bin/ - but note that a woven program has no window"
        echo "backend: charts need 'mill run'. Reports and captures are fine."
        ;;
    clean)
        rm -rf bin
        ;;
    *)
        echo "usage: ./build.sh [run SPEC|capture SPEC|test|build|clean]" >&2
        exit 2
        ;;
esac
