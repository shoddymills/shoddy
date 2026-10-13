#!/usr/bin/env bash
# Build the MCP lane: restore, build and test Sparky. Every call first
# runs the repo root's build, which publishes bin/mill - ShoddyWeave
# drives it. The .ps1 twin
# is the file that ships from the Windows workstation; keep the two in
# step.
#
#   ./build.sh              restore + build (Debug)
#   ./build.sh test         build + run Shoddy.Mcp.Tests
#   ./build.sh run          build + start the server on stdin/stdout
#   ./build.sh release      Release build - what a client should launch
#   ./build.sh publish [V]  self-contained single-file binaries, one
#                           archive per OS; V names them (sparky-V-RID)
#
# run is here for a person holding a terminal, and it will look like it
# has hung: the server is waiting for a JSON-RPC message on stdin, which
# is exactly what it should do. README.md has two lines that prove it is
# alive.
set -euo pipefail
cd "$(dirname "$0")"

# The root's build, every call: a check that bin/mill exists passed a mill
# from before a compiler change, and every sparky woven by it failed with
# "unknown word: TAKEN". build() in ../../build.sh has the whole story.
../../build.sh build

case "${1:-build}" in
    build)   dotnet build Sparky.slnx ;;
    test)    dotnet test Shoddy.Mcp.Tests ;;
    run)     dotnet build sparky && dotnet run --project sparky --no-build ;;
    release) dotnet build Sparky.slnx -c Release ;;
    publish)
        # What a release attaches: a self-contained single-file sparky
        # per OS, so the far end needs no repo and no .NET install.
        # Cross-targeting every RID from one runner is safe because the
        # lane is pure managed code - mcp.yml asserts that from outside.
        # The Windows RIDs ship as .zip; the unix RIDs as .tar.gz, because
        # the executable bit only survives a tar. win-arm64 is its own
        # archive so an ARM64 Windows machine runs sparky natively rather
        # than under x64 emulation.
        suffix="${2:+-$2}"
        pub=../../artifacts/publish
        mkdir -p "$pub"
        for rid in win-x64 win-arm64 linux-x64 osx-x64 osx-arm64; do
            dotnet publish sparky -c Release -r "$rid" --self-contained \
                -p:PublishSingleFile=true -o "$pub/sparky/$rid"
            if [ "${rid#win-}" != "$rid" ]; then
                (cd "$pub/sparky/$rid" && zip -qj "../../sparky$suffix-$rid.zip" sparky.exe)
            else
                chmod +x "$pub/sparky/$rid/sparky"
                tar -czf "$pub/sparky$suffix-$rid.tar.gz" -C "$pub/sparky/$rid" sparky
            fi
        done
        ls -1 "$pub" | sed 's/^/  -> /' ;;
    *)       echo "unknown command: $1 (build | test | run | release | publish)" >&2; exit 1 ;;
esac
