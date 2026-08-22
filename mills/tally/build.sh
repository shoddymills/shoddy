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
# EVERY TARGET RUNS FROM THIS DIRECTORY, which is how every other mill
# works and what makes tally usable by somebody sitting in this folder.
# SPEC defaults to files/grades.spec and a SPEC you name resolves the
# same way, so ./build.sh run files/marks.spec does what it reads like
# from here.
#
# Paths INSIDE a spec — data.file, window.capture — are resolved by
# tally against the current directory, so the shipped specs name
# files/... to match. That is the same rule demographics and iris use for
# their dat/... paths, and the reason all three work wherever you invoke
# the script from.
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
cd "$(dirname "$0")"
. ../../scripts/shoddy-mill-common.sh

SPEC=${2:-files/grades.spec}

case "${1:-run}" in
    run)
        ensure_mill
        "$MILL" run tally.shoddy "$SPEC"
        ;;
    capture)
        ensure_mill
        "$MILL" run --no-window tally.shoddy "$SPEC"
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy < /dev/null
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
