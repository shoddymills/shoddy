#!/usr/bin/env bash
# Build / run the demographics mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
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
# Neither takes arguments - the data and model paths are fixed at dat/,
# relative to this directory, and train/run always run from here
# regardless of where you invoke this script from: the model is the
# mill's own asset, not something to relocate by choice of cwd. Train
# first; the predictor aborts (politely) without dat/people-model.bin.
set -euo pipefail
cd "$(dirname "$0")"
. ../../scripts/mill-common.sh

TRAIN_OUT=bin/demographics-train.dll
RUN_OUT=bin/demographics.dll

build() {
    weave_and_move demographics-train.shoddy demographics.shoddy
    echo "built -> $TRAIN_OUT, $RUN_OUT"
}

case "${1:-build}" in
    build)
        build
        ;;
    train)
        [ -f "$TRAIN_OUT" ] || build
        dotnet "$TRAIN_OUT"
        ;;
    run)
        [ -f "$RUN_OUT" ] || build
        dotnet "$RUN_OUT"
        ;;
    test)
        # Run from source, not from bin/: the point is to grade what is
        # in the tree. No training - nine minutes, and it would rewrite
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
