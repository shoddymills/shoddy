#!/usr/bin/env bash
# Build / run the demographics mill (Unix: Linux / macOS / WSL).
# Windows users: use build.cmd (same commands).
#
#   ./build.sh            build the program into bin/
#   ./build.sh build      same as above
#   ./build.sh run        build if needed, then run the demo
#   ./build.sh clean      remove built binaries from bin/
#
# The build weaves demographics.shoddy to a self-contained assembly and
# drops every binary (the program, its runtimeconfig, Shoddy.Runtime.dll)
# into bin/. To just run an already-built program, no rebuild:
#
#   dotnet bin/demographics.dll
#
# Run takes no arguments — the data paths are fixed at dat/, relative
# to this directory, so run from here.
set -euo pipefail
cd "$(dirname "$0")"

REPO=../..
MILL=$REPO/bin/mill
SRC=demographics.shoddy
OUT=bin/demographics.dll

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

build() {
    ensure_mill
    "$MILL" weave "$SRC"
    mkdir -p bin
    mv -f demographics.dll demographics.runtimeconfig.json bin/
    mv -f Shoddy.*.dll bin/ 2>/dev/null || true
    echo "built -> $OUT"
}

case "${1:-build}" in
    build)
        build
        ;;
    run)
        [ -f "$OUT" ] || build
        dotnet "$OUT"
        ;;
    clean)
        rm -f bin/*.dll bin/*.json
        echo "cleaned."
        ;;
    *)
        echo "usage: ./build.sh [build|run|clean]" >&2
        exit 2
        ;;
esac
