#!/usr/bin/env bash
# Build / run the demographics mill (Unix: Linux / macOS / WSL).
# Windows users: use build.cmd (same commands).
#
#   ./build.sh            build both programs into bin/
#   ./build.sh build      same as above
#   ./build.sh train      build if needed, then train the model
#                         (writes dat/people-model.bin; ~9 minutes)
#   ./build.sh run        build if needed, then predict interactively
#   ./build.sh test       grade the shipped model against the data files
#   ./build.sh clean      remove built binaries from bin/
#
# The build weaves demographics-train.shoddy (the trainer) and
# demographics.shoddy (the predictor) to self-contained assemblies and
# drops every binary (the programs, runtimeconfigs, Shoddy.Runtime.dll)
# into bin/. To just run already-built programs, no rebuild:
#
#   dotnet bin/demographics-train.dll
#   dotnet bin/demographics.dll
#
# Neither takes arguments - the data and model paths are fixed at
# dat/, relative to this directory, so run from here. Train first;
# the predictor aborts (politely) without dat/people-model.bin.
set -euo pipefail
cd "$(dirname "$0")"

REPO=../..
MILL=$REPO/bin/mill
TRAINOUT=bin/demographics-train.dll
RUNOUT=bin/demographics.dll

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

build() {
    ensure_mill
    "$MILL" weave demographics-train.shoddy
    "$MILL" weave demographics.shoddy
    mkdir -p bin
    mv -f demographics-train.dll demographics-train.runtimeconfig.json bin/
    mv -f demographics.dll demographics.runtimeconfig.json bin/
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
    test)
        # Run from source, not from bin/: the point is to grade what is
        # in the tree. No training — nine minutes, and it would rewrite
        # the model being graded. test.shoddy says so at more length.
        ensure_mill
        "$MILL" run test.shoddy
        ;;
    clean)
        rm -f bin/*.dll bin/*.json
        echo "cleaned."
        ;;
    *)
        echo "usage: ./build.sh [build|train|run|test|clean]" >&2
        exit 2
        ;;
esac
