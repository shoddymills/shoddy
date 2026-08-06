#!/usr/bin/env bash
# Build / run the halifax mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh run              the calculator, at a prompt
#   ./build.sh test             the headless suite: no terminal needed
#   ./build.sh demo             the golden session, fed from files/demo.halifax
#   ./build.sh build            weave the program into bin/
#   ./build.sh clean            remove bin/
#
# run and demo both execute from the REPO ROOT, so a path you type at the
# prompt — SAVE "mine.halifax" — lands there, and so does the halifaxrc
# the shell looks for on the way up. That is the same rule tally uses for
# its spec paths: relative to where you ran from, not to where the mill
# happens to live.
#
# demo feeds files/demo.halifax to the prompt: the R5.2 sequence, in
# order, ending on a traced cascade. It is there to show the shell works
# end to end, not to look like a session — redirected input is not echoed
# back, so each answer starts on the prompt line that asked for it. In a
# terminal your own keystrokes fill that gap, which is what the docs
# page's transcript shows and what the demo GIF is recorded from.
#
# The test target needs NOTHING — no terminal, no display, no network.
# Everything between a typed line and the lines it produces is pure,
# which is the whole reason halifax-core.shoddy and halifax.shoddy are
# separate files.
set -euo pipefail
cd "$(dirname "$0")"

REPO=../..
MILL=$REPO/bin/mill

ensure_mill() {
    [ -x "$MILL" ] || {
        echo "mill toolchain not built; building it into $REPO/bin ..."
        dotnet publish "$REPO/src/Shoddy.Mill" -c Release -o "$REPO/bin"
    }
}

case "${1:-run}" in
    run)
        ensure_mill
        ( cd "$REPO" && bin/mill run mills/halifax/halifax.shoddy )
        ;;
    test)
        ensure_mill
        ( cd "$REPO" && bin/mill run mills/halifax/test.shoddy < /dev/null )
        ;;
    demo)
        ensure_mill
        ( cd "$REPO" && bin/mill run mills/halifax/halifax.shoddy \
              < mills/halifax/files/demo.halifax )
        ;;
    build)
        ensure_mill
        # weave writes BESIDE the source and has no -o flag; this moves the
        # result into bin/, the same shuffle the other weaving mills do.
        "$MILL" weave halifax.shoddy
        mkdir -p bin
        mv -f halifax.dll halifax.runtimeconfig.json bin/
        mv -f Shoddy.*.dll bin/ 2>/dev/null || true
        echo "woven into bin/"
        ;;
    clean)
        rm -rf bin
        ;;
    *)
        echo "usage: ./build.sh [run|test|demo|build|clean]" >&2
        exit 2
        ;;
esac
