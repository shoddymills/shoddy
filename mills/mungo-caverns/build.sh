#!/usr/bin/env bash
# Run Mungo Caverns (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh          play the game
#   ./build.sh run      same as above
#   ./build.sh test     run every headless suite (test.shoddy, tests/test-*)
#
# mungo-caverns is a console program: prompts in, text out, no window. Words
# are significant to five letters, as they have been since 1977 -- XYZZY,
# PLUGH, and PLOVER all still work.
#
# The cave lives in the mungo-caverns-*.shoddy tables, which are
# hand-edited source.
#
# Testing is five headless suites.  test.shoddy covers the generator, the
# tables and the parser; tests/test-tables.shoddy asserts what must be
# true of the cave itself; tests/test-turn.shoddy drives whole turns as a
# function; tests/test-walk.shoddy plays a game to the gold and back; and
# tests/test-fuzz.shoddy throws random commands at it.  194 assertions,
# about twenty seconds, and "test" runs the lot.  See tests/README.
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
        for suite in test.shoddy tests/test-*.shoddy; do
            echo "--- $suite"
            "$MILL" run "$suite" < /dev/null || exit 1
        done
        ;;
    *)
        echo "usage: ./build.sh [run|test]" >&2
        exit 2
        ;;
esac
