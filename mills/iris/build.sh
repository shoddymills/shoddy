#!/usr/bin/env bash
# Build / run the iris mill (Unix: Linux / macOS / WSL).
# Windows users: use build.cmd (same commands).
#
#   ./build.sh            build both programs into bin/
#   ./build.sh build      same as above
#   ./build.sh train      build if needed, then train the model
#                         (writes dat/iris-model.bin; a few seconds)
#   ./build.sh run        build if needed, then classify interactively
#   ./build.sh clean      remove built binaries from bin/
#
# The build weaves iris-train.shoddy (the trainer) and iris.shoddy (the
# predictor) to self-contained assemblies and drops every binary (the
# programs, runtimeconfigs, Shoddy.Runtime.dll) into bin/. To just run
# already-built programs, no rebuild:
#
#   dotnet bin/iris-train.dll
#   dotnet bin/iris.dll
#
# Neither takes arguments - the data and model paths are fixed at dat/,
# relative to this directory, so run from here. Train first; the
# predictor aborts (politely) without dat/iris-model.bin.
#
# This is the classification counterpart to the demographics mill.
# Demographics predicts a number and is scored on how close it gets;
# this predicts a category, is scored on how often it is right, and
# reports how sure it was. Training takes seconds rather than nine
# minutes, which makes it the one to reach for when changing
# machines/neural.shoddy and wanting to know quickly whether it still
# works.
set -euo pipefail
cd "$(dirname "$0")"

REPO=../..
MILL=$REPO/bin/mill
TRAINOUT=bin/iris-train.dll
RUNOUT=bin/iris.dll

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

build() {
    ensure_mill
    "$MILL" weave iris-train.shoddy
    "$MILL" weave iris.shoddy
    mkdir -p bin
    mv -f iris-train.dll iris-train.runtimeconfig.json bin/
    mv -f iris.dll iris.runtimeconfig.json bin/
    mv -f Shoddy.*.dll bin/ 2>/dev/null || true
    echo "built -> $TRAINOUT, $RUNOUT"
}

case "${1:-build}" in
    build)
        build
        ;;
    train)
        [ -f "$TRAINOUT" ] || build
        dotnet "$TRAINOUT"
        ;;
    run)
        [ -f "$RUNOUT" ] || build
        dotnet "$RUNOUT"
        ;;
    clean)
        rm -f bin/*.dll bin/*.json
        echo "cleaned."
        ;;
    *)
        echo "usage: ./build.sh [build|train|run|clean]" >&2
        exit 2
        ;;
esac
