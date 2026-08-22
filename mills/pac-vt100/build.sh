#!/usr/bin/env bash
# Run Shoddy Pac (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh          run the game
#   ./build.sh test     run the headless simulation smoke check
#
# Pac is a VT100 terminal program — no scribbler, no window: it draws
# with escape sequences and reads keys through the InKey builtin, so it
# wants a real ANSI-capable terminal at least 80x26. The pure model in
# pac-core.shoddy is what the unit tests cover; this wrapper just
# launches the game. Controls: WASD / arrows / keypad move, Q or Escape
# quits.
set -euo pipefail
cd "$(dirname "$0")"
. ../../scripts/shoddy-mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        "$MILL" run pac.shoddy
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy
        ;;
    *)
        echo "usage: ./build.sh [run|test]" >&2
        exit 2
        ;;
esac
