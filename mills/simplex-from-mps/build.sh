#!/usr/bin/env bash
# Build / run the simplex-from-mps mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh            build the program into bin/
#   ./build.sh build      same as above
#   ./build.sh run FILE   build if needed, then run on an MPS FILE
#   ./build.sh test       solve both fixtures and check the answers
#   ./build.sh clean      remove built binaries from bin/
#
# The build weaves simplex-mps.shoddy to a self-contained assembly and
# drops every binary (the program, its runtimeconfig, Shoddy.Runtime.dll)
# into bin/. To just run an already-built program, no rebuild:
#
#   dotnet bin/simplex-mps.dll files/blend.mps        # or files/mix.mps
#
# Add -z (or --zero-lower) to force x >= 0 on every variable.
set -euo pipefail
cd "$(dirname "$0")"

REPO=../..
MILL=$REPO/bin/mill
SRC=simplex-mps.shoddy
OUT=bin/simplex-mps.dll

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
    mv -f simplex-mps.dll simplex-mps.runtimeconfig.json bin/
    mv -f Shoddy.*.dll bin/ 2>/dev/null || true
    echo "built -> $OUT"
}

case "${1:-build}" in
    build)
        build
        ;;
    run)
        [ $# -ge 2 ] || { echo "usage: ./build.sh run FILE.mps [-z]" >&2; exit 2; }
        [ -f "$OUT" ] || build
        shift
        dotnet "$OUT" "$@"
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy
        ;;
    clean)
        rm -f bin/*.dll bin/*.json
        echo "cleaned."
        ;;
    *)
        echo "usage: ./build.sh [build|run FILE.mps|test|clean]" >&2
        exit 2
        ;;
esac
