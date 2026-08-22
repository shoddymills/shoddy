#!/usr/bin/env bash
# Build / run the sparky mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh run              the dictionary, at a prompt
#   ./build.sh test             the headless suite: no terminal needed
#   ./build.sh build            weave the program into bin/
#
# Sparky's product is an MCP server (hosts/mcp) and its caller is a
# model. What this script builds is the mill underneath it: the same
# fold, the same dictionary, reachable by a person at a terminal. run is
# here so the fold can be looked at, and test is the gate that says the
# fold is still what it was.
#
# run is LIVE: it launches from wherever you actually typed ./build.sh,
# so sparkyrc on the way in resolves against your own shell, exactly as
# it would running the mill directly. test stays pinned to this
# directory: it reads the mill's own suite, not anything you typed.
#
# The test target needs NOTHING - no terminal, no display, no network,
# and it writes no files at all. Everything between a typed line and the
# lines it produces is pure, which is the whole reason
# sparky-core.shoddy and sparky.shoddy are separate files.
set -euo pipefail
ORIG_DIR=$(pwd)
cd "$(dirname "$0")"
. ../../scripts/shoddy-mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        run_live "$MILL" run "$MILL_DIR/sparky.shoddy"
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy < /dev/null
        ;;
    build)
        weave_and_move sparky.shoddy
        echo "woven into bin/"
        ;;
    *)
        echo "usage: ./build.sh [run|test|build]" >&2
        exit 2
        ;;
esac
