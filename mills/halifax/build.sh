#!/usr/bin/env bash
# Build / run the halifax mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh run              the calculator, at a prompt
#   ./build.sh test             the headless suite: no terminal needed
#   ./build.sh demo             the golden session, fed from files/demo.halifax
#   ./build.sh build            weave the program into bin/
#
# run is LIVE: it launches from wherever you actually typed ./build.sh,
# not from this directory - so a path you give SAVE, LOAD or TAPESAVE at
# the prompt, and halifaxrc on the way in, resolve against your own
# shell, exactly as they would running the calculator directly. test and
# demo stay pinned to this directory: they read the mill's own fixtures
# (test.shoddy, files/demo.halifax), not anything you typed.
#
# demo feeds files/demo.halifax to the prompt: the R5.2 sequence, in
# order, ending on a traced cascade. It is there to show the shell works
# end to end, not to look like a session - redirected input is not echoed
# back, so each answer starts on the prompt line that asked for it. In a
# terminal your own keystrokes fill that gap, which is what the docs
# page's transcript shows and what the demo GIF is recorded from.
#
# The test target needs NOTHING - no terminal, no display, no network.
# Everything between a typed line and the lines it produces is pure,
# which is the whole reason halifax-core.shoddy and halifax.shoddy are
# separate files.
set -euo pipefail
ORIG_DIR=$(pwd)
cd "$(dirname "$0")"
. ../../scripts/mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        run_live "$MILL" run "$MILL_DIR/halifax.shoddy"
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy < /dev/null
        ;;
    demo)
        ensure_mill
        "$MILL" run halifax.shoddy < files/demo.halifax
        ;;
    build)
        weave_and_move halifax.shoddy
        echo "woven into bin/"
        ;;
    *)
        echo "usage: ./build.sh [run|test|demo|build]" >&2
        exit 2
        ;;
esac
