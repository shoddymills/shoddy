#!/usr/bin/env bash
# Run Shoddy Invaders (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh          run the game
#   ./build.sh run      same as above
#   ./build.sh test     run the headless simulation smoke check
#
# Invaders is a scribbler program: it opens a window, so it runs under
# `mill run` (a woven `dotnet FILE.dll` has no window backend). The pure
# model in invaders-core.shoddy is what the unit tests cover; this wrapper
# just launches the windowed game. Controls: Left/Right move, Space fires,
# Q or Escape quits.
set -euo pipefail
cd "$(dirname "$0")"
. ../../scripts/mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        "$MILL" run invaders.shoddy
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
