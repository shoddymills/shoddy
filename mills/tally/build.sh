#!/usr/bin/env bash
# Build / run the tally mill (Unix: Linux / macOS / WSL).
# Windows users: run this via Git Bash or WSL (same commands).
#
#   ./build.sh run [SPEC]       read the spec, print the report, show the chart
#   ./build.sh capture [SPEC]   the same, with nothing on screen — the PNG is the output
#   ./build.sh test             the headless suite: no window, no display
#   ./build.sh build            weave the program into bin/
#   ./build.sh clean            remove bin/
#
# SPEC defaults to files/grades.spec. Paths INSIDE a spec (data.file,
# window.capture) are relative to the directory you run from, which the
# run target makes the repo root — so a spec written for ./build.sh says
# mills/tally/files/... The shipped specs do exactly that.
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

REPO=../..
MILL=$REPO/bin/mill
SPEC=${2:-mills/tally/files/grades.spec}

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

case "${1:-run}" in
    run)
        ensure_mill
        ( cd "$REPO" && bin/mill run mills/tally/tally.shoddy "$SPEC" )
        ;;
    capture)
        ensure_mill
        ( cd "$REPO" && bin/mill run --no-window mills/tally/tally.shoddy "$SPEC" )
        ;;
    test)
        ensure_mill
        ( cd "$REPO" && bin/mill run mills/tally/test.shoddy < /dev/null )
        ;;
    build)
        ensure_mill
        mkdir -p bin
        "$MILL" weave tally.shoddy -o bin/tally.dll
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
