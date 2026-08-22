# scripts/shoddy-mill-common.sh - shared plumbing for every mill's build.sh.
#
# Source this AFTER `cd`-ing into the mill's own directory, and after
# setting ORIG_DIR to the directory the caller actually invoked build.sh
# from (captured before that cd - see any build.sh for the two-line
# preamble). Provides:
#
#   $MILL_DIR       this mill's own absolute directory
#   $MILL           the mill toolchain, resolved from MILL_DIR
#   ensure_mill     builds the toolchain into ../../bin if it is missing
#   run_live CMD...   runs CMD from ORIG_DIR, not from the mill's folder -
#                     for any target that launches a program a person
#                     interacts with, so a path they type or pass at the
#                     prompt resolves where THEY are sitting
#   run_from_repo CMD...   runs CMD from the repo root specifically - for
#                     the handful of test suites (tally, weather-glass)
#                     whose own source hardcodes repo-root-relative
#                     fixture paths rather than mill-relative ones. Not a
#                     substitute for run_live and not the default: most
#                     mills' tests stay pinned to MILL_DIR below.
#   weave_and_move SRC...   weaves one or more .shoddy sources (found
#                     beside this script) and moves every resulting
#                     .dll/.runtimeconfig.json, plus any Shoddy.*.dll,
#                     into ./bin
#
# The rule this buys: run/capture/train/demo - anything that puts a live
# program in front of you - go through run_live, so a path resolves
# against wherever you actually typed ./build.sh. test/build/clean never
# do: they read the mill's own fixtures and write the mill's own build
# output, and moving those because of where you happened to invoke the
# script from would be the surprise, not the fix.

MILL_DIR=$(pwd)
REPO=$MILL_DIR/../..
MILL=$REPO/bin/mill

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

run_live() {
    ( cd "$ORIG_DIR" && "$@" )
}

run_from_repo() {
    ( cd "$REPO" && "$@" )
}

weave_and_move() {
    ensure_mill
    for src in "$@"; do
        "$MILL" weave "$src"
    done
    mkdir -p bin
    for src in "$@"; do
        base=${src%.shoddy}
        mv -f "$base.dll" "$base.runtimeconfig.json" bin/
    done
    mv -f Shoddy.*.dll bin/ 2>/dev/null || true
}
