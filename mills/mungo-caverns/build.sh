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
# run is LIVE: it launches from wherever you actually typed ./build.sh,
# not from this directory - so a save-file name you type in-game (SAVE,
# RESTORE) resolves against your own shell, not the mill's folder. test
# stays pinned to this directory: it reads the mill's own fixture suites.
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
ORIG_DIR=$(pwd)
cd "$(dirname "$0")"
. ../../scripts/mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        run_live "$MILL" run "$MILL_DIR/mungo-caverns.shoddy"
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
