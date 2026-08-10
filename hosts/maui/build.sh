#!/usr/bin/env bash
# Build the MAUI lane: restore, build and test whatever this machine's
# workloads can build. Run the repo root's build first so bin/mill
# exists — ShoddyWeave drives it. The .ps1 twin is the file that ships
# from the Windows workstation; keep the two in step.
#
#   ./build.sh          restore + build (Debug)
#   ./build.sh test     build + run Shoddy.Maui.Tests
#   ./build.sh run      build + launch the Reckoner app
#   ./build.sh release  Release build (release artifacts carry no perch - B7.4)
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -e ../../bin/mill ] && [ ! -e ../../bin/mill.exe ]; then
    echo "STOPPED: bin/mill is missing - run ./build.sh at the repo root first." >&2
    exit 1
fi

case "${1:-build}" in
    build)   dotnet build ShoddyMaui.slnx ;;
    test)    dotnet test Shoddy.Maui.Tests ;;
    run)     dotnet build shoddy-reckoner && dotnet run --project shoddy-reckoner --no-build ;;
    release) dotnet build ShoddyMaui.slnx -c Release ;;
    *)       echo "unknown command: $1 (build | test | run | release)" >&2; exit 1 ;;
esac
