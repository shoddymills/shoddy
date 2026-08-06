#!/usr/bin/env bash
# Build / run the emley-moor mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh            serve on http://127.0.0.1:8080/
#   ./build.sh run        same as above
#   ./build.sh test       the routing tests - no network, no --allow-net
#   ./build.sh build      weave the server to bin/
#   ./build.sh clean      remove bin/
#
# The server needs --allow-net, because the network is a gated capability
# and a web server is the most network a program can be. It binds the
# LOOPBACK only: reachable from this machine and nowhere else. Editing
# that to ListenOn("0.0.0.0", ...) puts a plaintext HTTP server written
# over a weekend on a public address, so do not.
#
# The test target needs nothing. Every route is graded by calling
# Respond(request) with fixture text, because the server is a pure
# function of the request and the socket is twenty lines beside it. That
# split is the point of the mill; the tests are what it buys.
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
        "$MILL" run --allow-net emley-moor.shoddy
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy < /dev/null
        ;;
    build)
        ensure_mill
        # weave writes BESIDE the source and has no -o flag; this moves the
        # result into bin/, the same shuffle the other weaving mills do.
        "$MILL" weave emley-moor.shoddy
        mkdir -p bin
        mv -f emley-moor.dll emley-moor.runtimeconfig.json bin/
        mv -f Shoddy.*.dll bin/ 2>/dev/null || true
        echo "woven into bin/ - run with: dotnet bin/emley-moor.dll (needs SHODDY_ALLOW_NET=1)"
        ;;
    clean)
        rm -rf bin
        ;;
    *)
        echo "usage: ./build.sh [run|test|build|clean]" >&2
        exit 2
        ;;
esac
