#!/usr/bin/env bash
# Build the Fettler lane: restore, build and test fettle.
#
#   ./build.sh              restore + build (Debug)
#   ./build.sh test         build + run Fettler.Tests and burler.Tests
#   ./build.sh release      Release build - what a client should launch
#   ./build.sh publish [V]  self-contained single-file binaries, one
#                           archive per OS per program; V names them
#                           (fettle-V-RID, burler-V-RID)
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
    test)
        # Both, and both every time. burler is a separate project because
        # its ONNX dependency may not enter Fettler's package allowlist,
        # and a lane whose second test project is only run when somebody
        # remembers is a lane with an untested half.
        run test Fettler.Tests
        run test burler.Tests
        ;;
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
        pub="../artifacts/publish"

        # TWO programs, each its own archive. burler is optional and is
        # only wanted by somebody who has switched the disclosure screen
        # on, so folding it into fettle's archive would make every
        # download pay for ONNX Runtime to get a feature most trees never
        # use. fettle looks for it BESIDE ITSELF, so the two unpack into
        # one directory when both are wanted.
        for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
            for program in fettle burler; do
                dir="$pub/$program/$rid"

                # burler bundles ONNX Runtime, which is native per RID.
                # Without this the natives land beside the executable
                # instead of inside it, and an archive carrying only the
                # one file would be missing them.
                native=""
                [ "$program" = "burler" ] && native="-p:IncludeNativeLibrariesForSelfExtract=true"

                run publish "$program" -c Release -r "$rid" --self-contained \
                    -p:PublishSingleFile=true ${native:+"$native"} -o "$dir"

                # Two licence obligations, and both are met IN THE ARCHIVE -
                # not merely in the repository, which a person downloading a
                # release never sees. Apache-2.0 asks the licence and any
                # NOTICE to travel with a distributed binary, and fettle
                # bundles PdfPig into the one file it ships; burler bundles
                # ONNX Runtime and Microsoft.ML.Tokenizers, both MIT. MIT
                # asks its own copyright notice to be in every copy, and this
                # archive is a copy. An obligation nobody can read has not
                # been met.
                cp ../NOTICE "$dir/NOTICE"
                cp ../LICENSE "$dir/LICENSE"

                if [ "$rid" = "win-x64" ]; then
                    (cd "$dir" && zip -q "../../$program$suffix-$rid.zip" "$program.exe" NOTICE LICENSE)
                else
                    chmod +x "$dir/$program"
                    tar -czf "$pub/$program$suffix-$rid.tar.gz" -C "$dir" "$program" NOTICE LICENSE
                fi
            done
        done
        ls -1 "$pub" | grep -E '^(fettle|burler)' | sed 's/^/  -> /'
        ;;
    *)
        echo "unknown command: $command (build | test | release | publish)" >&2
        exit 1
        ;;
esac
