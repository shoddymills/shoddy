#!/usr/bin/env bash
# Build the Fettler lane: restore, build and test fettle.
#
#   ./build.sh              restore + build (Debug)
#   ./build.sh test         build + run Fettler.Tests
#   ./build.sh release      Release build - what a client should launch
#   ./build.sh publish [V]  self-contained single-file binaries, one
#                           archive per OS; V names them (fettle-V-RID)
#
# The twin of build.ps1 and equivalent to it. Unlike every other lane
# here this one needs NOTHING built first: R1.2 forbids Fettler to
# reference any other project in this repository, so there is no
# bin/mill to publish and no mill to weave.
set -euo pipefail
cd "$(dirname "$0")"

command=${1:-build}
version=${2:-}

run() {
    echo "> dotnet $*"
    dotnet "$@"
}

case "$command" in
    build)   run build Fettler.slnx ;;
    test)    run test Fettler.Tests ;;
    release) run build Fettler.slnx -c Release ;;
    publish)
        # R2.3: a self-contained single-file fettle per OS, so the target
        # machine needs no .NET install and no repository checkout.
        #
        # win-x64 ships as .zip; the unix RIDs as .tar.gz, because the
        # executable bit only survives a tar. Cut here on a unix runner
        # the bit is real, which is why a release attaches these and not
        # the ones a Windows machine makes.
        suffix=""
        [ -n "$version" ] && suffix="-$version"
        pub="../../artifacts/publish"
        for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
            dir="$pub/fettle/$rid"
            run publish fettle -c Release -r "$rid" --self-contained \
                -p:PublishSingleFile=true -o "$dir"
            if [ "$rid" = "win-x64" ]; then
                (cd "$dir" && zip -q "../../fettle$suffix-$rid.zip" fettle.exe)
            else
                chmod +x "$dir/fettle"
                tar -czf "$pub/fettle$suffix-$rid.tar.gz" -C "$dir" fettle
            fi
        done
        ls -1 "$pub" | grep '^fettle' | sed 's/^/  -> /'
        ;;
    *)
        echo "unknown command: $command (build | test | release | publish)" >&2
        exit 1
        ;;
esac
