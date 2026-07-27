#!/usr/bin/env bash
# Run Colossal Cave Adventure (Unix: Linux / macOS / WSL).
# Windows users: use build.cmd (same commands).
#
#   ./build.sh          play the game
#   ./build.sh run      same as above
#   ./build.sh test     run the headless model checks
#   ./build.sh gen      regenerate cave-data.shoddy from adventure.yaml
#
# open-cave is a console program: prompts in, text out, no window. The
# pure model in cave-core.shoddy is what test.shoddy covers; this wrapper
# just launches the game. Words are significant to five letters, as they
# have been since 1977 -- XYZZY, PLUGH, and PLOVER all still work.
#
# The cave itself lives in cave-data.shoddy, generated from
# adventure.yaml by gen.shoddy and committed, so playing needs nothing
# but the mill. The generator is Shoddy too: it reads the 154K YAML,
# resolves its anchors, and writes the tables out as Shoddy source.
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
        "$MILL" run cave.shoddy
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy
        ;;
    gen)
        ensure_mill
        "$MILL" run gen.shoddy
        ;;
    *)
        echo "usage: ./build.sh [run|test|gen]" >&2
        exit 2
        ;;
esac
