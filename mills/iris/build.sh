#!/usr/bin/env bash
# Build / run the iris mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh            build both programs into bin/
#   ./build.sh build      same as above
#   ./build.sh train      build if needed, then train the model
#                         (writes dat/iris-model.bin; a few seconds)
#   ./build.sh run        build if needed, then classify interactively
#   ./build.sh test       grade the shipped model against the held-out rows
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
# relative to this directory, and train/run always run from here
# regardless of where you invoke this script from: the model is the
# mill's own asset, not something to relocate by choice of cwd. Train
# first; the predictor aborts (politely) without dat/iris-model.bin.
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
. ../../scripts/shoddy-mill-common.sh

TRAIN_OUT=bin/iris-train.dll
RUN_OUT=bin/iris.dll

build() {
    weave_and_move iris-train.shoddy iris.shoddy
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
        # in the tree. No training — test.shoddy explains why.
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
