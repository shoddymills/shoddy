#!/usr/bin/env bash
# Run Devil's Dust (Unix: Linux / macOS / WSL).
# Windows users: run this via Git Bash or WSL (same commands).
#
#   ./build.sh          run the demo
#   ./build.sh run      same as above
#   ./build.sh test     run the headless model checks
#
# Devil's Dust is a scribbler program: it opens a window, so it runs
# under `mill run` (a woven `dotnet FILE.dll` has no window backend).
# The pure model in devils-dust-core.shoddy is what the tests cover;
# this wrapper just launches the windowed demo. Q or Escape quits.
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
        "$MILL" run devils-dust.shoddy
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
