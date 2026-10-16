# scripts/shoddy-mill-common.sh - shared plumbing for every mill's build.sh.
#
# Source this AFTER `cd`-ing into the mill's own directory, and after
# setting ORIG_DIR to the directory the caller actually invoked build.sh
# from (captured before that cd - see any build.sh for the two-line
# preamble). Provides:
#
#   $MILL_DIR       this mill's own absolute directory
#   $MILL           the full toolchain, where `dotnet build` puts it
#   ensure_mill     builds the toolchain and the machine library it
#                   carries, through the repo root's build.sh; MSBuild's
#                   own up-to-date check makes a second call free
#   run_live CMD...   runs CMD from ORIG_DIR, not from the mill's folder -
#                     for any target that launches a program a person
#                     interacts with, so a path they type or pass at the
#                     prompt resolves where THEY are sitting
#   weave_and_move SRC...   weaves one or more .shoddy sources (found
#                     beside this script) into ./bin: the program, its
#                     runtime config, the runtime and the libraries it
#                     links. `mill weave` writes there itself now; the
#                     name stays so every mill's script reads as before.
#
# The rule this buys: run/capture/train/demo - anything that puts a live
# program in front of you - go through run_live, so a path resolves
# against wherever you actually typed ./build.sh. test/build/clean never
# do: they read the mill's own fixtures and write the mill's own build
# output, and moving those because of where you happened to invoke the
# script from would be the surprise, not the fix.

MILL_DIR=$(pwd)
REPO=$MILL_DIR/../..
MILL=$REPO/artifacts/bin/Shoddy.Mill/release/mill
# The checkout's own machine sources are the library this mill's
# relative includes reach; see the root build.sh.
export SHODDYLIB="$REPO/machines"

ensure_mill() {
    "$REPO/build.sh" build
}

run_live() {
    ( cd "$ORIG_DIR" && "$@" )
}

weave_and_move() {
    ensure_mill
    for src in "$@"; do
        "$MILL" weave "$src"
    done
}
