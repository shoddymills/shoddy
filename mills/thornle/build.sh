#!/usr/bin/env bash
# Run Thornle (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh          run the game
#   ./build.sh run      same as above
#   ./build.sh short    run the game on files/short-list.txt, six words
#   ./build.sh test     run the headless checks
#
# Thornle is a VT100 terminal program: no scribbler, no window. It draws
# with escape sequences and reads each guess as a whole line through the
# InputLine builtin, so it wants an ANSI-capable terminal at least 80x24.
# The pure model in thornle-core.shoddy is what the checks cover; this
# wrapper just launches the game.
set -euo pipefail
cd "$(dirname "$0")"
. ../../scripts/shoddy-mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        "$MILL" run thornle.shoddy
        ;;
    short)
        ensure_mill
        "$MILL" run thornle.shoddy files/short-list.txt
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy < /dev/null
        ;;
    *)
        echo "usage: ./build.sh [run|short|test]" >&2
        exit 2
        ;;
esac
