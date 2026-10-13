# scripts/shoddy-mill-common.sh - shared plumbing for every mill's build.sh.
#
# Source this AFTER `cd`-ing into the mill's own directory, and after
# setting ORIG_DIR to the directory the caller actually invoked build.sh
# from (captured before that cd - see any build.sh for the two-line
# preamble). Provides:
#
#   $MILL_DIR       this mill's own absolute directory
#   $MILL           the mill toolchain, resolved from MILL_DIR
#   ensure_mill     publishes the toolchain into ../../bin, every call,
#                   through the repo root's build.sh
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

# The root's `build`, not a copy of it: that is the one place that
# publishes the mill and empties machine DLLs woven by an older one. It
# used to be an existence check here, which kept a stale mill forever -
# build() in the root build.sh has the whole story.
ensure_mill() {
    "$REPO/build.sh" build
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
