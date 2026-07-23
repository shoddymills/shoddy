#!/usr/bin/env bash
# Run The Oregon Trail (Unix: Linux / macOS / WSL).
# Windows users: use build.cmd (same commands).
#
#   ./build.sh          run the game
#   ./build.sh run      same as above
#   ./build.sh test     run the headless model checks
#
# Oregon is a console program: prompts in, text out, no window. The
# pure model in oregon-core.shoddy is what test.shoddy covers; this
# wrapper just launches the interactive game. When told to TYPE a
# word (BANG, BLAM, POW, WHAM), type it fast and press Enter.
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
        "$MILL" run oregon.shoddy
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
