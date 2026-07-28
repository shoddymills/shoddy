#!/usr/bin/env bash
# Run Mungo Caverns (Unix: Linux / macOS / WSL).
# Windows users: use build.cmd (same commands).
#
#   ./build.sh          play the game
#   ./build.sh run      same as above
#   ./build.sh test     run the headless model checks
#   ./build.sh smoke    replay 16 recorded transcripts   (~3 minutes)
#   ./build.sh check    replay all 107                   (~18 minutes)
#
# mungo-caverns is a console program: prompts in, text out, no window. Words
# are significant to five letters, as they have been since 1977 -- XYZZY,
# PLUGH, and PLOVER all still work.
#
# The cave lives in the mungo-caverns-*.shoddy tables, which are hand-edited
# source: the YAML and the generator that first produced them are in
# obsolete/ and are not run. test.shoddy covers the pure model in
# mungo-caverns-core.shoddy; everything below the parser is covered by the
# recorded transcripts in tests/, which are the port's real
# specification. See tests/README.
set -euo pipefail
cd "$(dirname "$0")"

REPO=../..
MILL=$REPO/bin/mill

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

case "${1:-run}" in
    run)
        ensure_mill
        "$MILL" run mungo-caverns.shoddy
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy
        ;;
    smoke)
        ensure_mill
        tests/run.sh --smoke
        ;;
    check)
        ensure_mill
        tests/run.sh
        ;;
    *)
        echo "usage: ./build.sh [run|test|smoke|check]" >&2
        exit 2
        ;;
esac
