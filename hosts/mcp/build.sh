#!/usr/bin/env bash
# Build the MCP lane: restore, build and test Sparky. Run the repo root's
# build first so bin/mill exists — ShoddyWeave drives it. The .ps1 twin
# is the file that ships from the Windows workstation; keep the two in
# step.
#
#   ./build.sh          restore + build (Debug)
#   ./build.sh test     build + run Shoddy.Mcp.Tests
#   ./build.sh run      build + start the server on stdin/stdout
#   ./build.sh release  Release build - what a client should launch
#
# run is here for a person holding a terminal, and it will look like it
# has hung: the server is waiting for a JSON-RPC message on stdin, which
# is exactly what it should do. README.md has two lines that prove it is
# alive.
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -e ../../bin/mill ] && [ ! -e ../../bin/mill.exe ]; then
    echo "STOPPED: bin/mill is missing - run ./build.sh at the repo root first." >&2
    exit 1
fi

case "${1:-build}" in
    build)   dotnet build Sparky.slnx ;;
    test)    dotnet test Shoddy.Mcp.Tests ;;
    run)     dotnet build sparky && dotnet run --project sparky --no-build ;;
    release) dotnet build Sparky.slnx -c Release ;;
    *)       echo "unknown command: $1 (build | test | run | release)" >&2; exit 1 ;;
esac
