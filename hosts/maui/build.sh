#!/usr/bin/env bash
# Build the MAUI lane: restore, build and test whatever this machine's
# workloads can build. The lane's projects build the toolchain they
# weave with and reference the machine library, so a plain dotnet build
# is the whole build. The .ps1 twin is the file that ships from the
# Windows workstation; keep the two in step.
#
#   ./build.sh          restore + build (Debug)
#   ./build.sh test     build + run Shoddy.Maui.Tests
#   ./build.sh run      build + launch the Reckoner app
#   ./build.sh release  Release build (release artifacts carry no perch - B7.4)
set -euo pipefail
cd "$(dirname "$0")"

case "${1:-build}" in
    build)   dotnet build ShoddyMaui.slnx ;;
    test)    dotnet test Shoddy.Maui.Tests ;;
    run)     dotnet build shoddy-reckoner && dotnet run --project shoddy-reckoner --no-build ;;
    release) dotnet build ShoddyMaui.slnx -c Release ;;
    *)       echo "unknown command: $1 (build | test | run | release)" >&2; exit 1 ;;
esac
