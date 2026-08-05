#!/usr/bin/env bash
# Run the spirograph (Unix: Linux / macOS / WSL).
# Windows users: run this via Git Bash or WSL, same commands.
#
#   ./build.sh          draw the spiral in a window
#   ./build.sh run      same as above
#   ./build.sh test     run the headless checks (no window; works in CI)
#
# The drawing opens a window, so it runs under `mill run` — a woven
# `dotnet spiro.dll` has no window backend. The pure half in
# spiro-core.shoddy is what test.shoddy covers, and that needs no display.
#
# Finding a mill, in order: $MILL if you set it, the repo's own bin/mill
# when this folder sits inside a checkout, then whatever `mill` is on the
# PATH — the VS Code extension carries one. Copy this file beside your own
# program and it keeps working wherever the folder lives.
set -euo pipefail
cd "$(dirname "$0")"

find_mill() {
    if [ -n "${MILL:-}" ]; then echo "$MILL"; return; fi
    for candidate in ../../bin/mill ../../bin/mill.exe; do
        [ -x "$candidate" ] && { echo "$candidate"; return; }
    done
    if command -v mill >/dev/null 2>&1; then echo mill; return; fi
    echo "no mill found — set MILL=/path/to/mill, or install the VS Code" >&2
    echo "extension (it carries one) and use its Run button." >&2
    exit 1
}

MILL_BIN=$(find_mill)

case "${1:-run}" in
    run)  "$MILL_BIN" run spiro.shoddy ;;
    test) "$MILL_BIN" run test.shoddy ;;
    *)    echo "usage: ./build.sh [run|test]" >&2; exit 2 ;;
esac
