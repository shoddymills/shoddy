#!/usr/bin/env bash
# Shoddy build wrapper (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh build                 build the mill into bin/
#   ./build.sh test                  run the golden suite + libtest assertions
#   ./build.sh run FILE.shoddy       compile in memory and run a program
#   ./build.sh weave FILE.shoddy     compile a program to an assembly
#   ./build.sh machines              compile every machine to a machine DLL
#   ./build.sh stage                 stage the mill + machines into the extension
#   ./build.sh vsix [bump]           package the VS Code extension (.vsix)
#   ./build.sh clean                 remove build output
#   ./build.sh help                  show this help
#
# vsix [bump]: optional patch|minor|major or an exact X.Y.Z to bump the
# extension version before packaging (e.g. ./build.sh vsix patch).
# vsix stages first, so the package carries its own mill and machines.
set -euo pipefail
cd "$(dirname "$0")"

MILL=bin/mill
# Where `stage` drops the extension's own copy of the toolchain. Both are
# build output: gitignored, and rebuilt from scratch on every stage.
STAGE_MILL=vscode-shoddy/mill
STAGE_LIB=vscode-shoddy/machines

build() {
    dotnet publish src/Shoddy.Mill -c Release -o bin
}

ensure_mill() {
    [ -x "$MILL" ] || { echo "mill not built; running build first..."; build; }
}

machines() {
    ensure_mill
    # Dependency order: an Include "x.shoddy" resolves to the machine DLL
    # only if Shoddy.Machines.X.dll is already built — otherwise the
    # source is spliced in and its defs re-exported, which collides with
    # the real machine downstream (duplicate definition of ANY, etc.).
    pending=$(echo machines/*.shoddy)
    built=" "
    while [ -n "$pending" ]; do
        progress=
        remaining=
        for m in $pending; do
            stem=$(basename "$m" .shoddy | tr 'A-Z' 'a-z')
            unmet=
            for d in $(sed -n 's/^[[:space:]]*Include[[:space:]]*"\(.*\)\.shoddy".*/\1/p' "$m" | tr 'A-Z' 'a-z'); do
                [ -f "machines/$d.shoddy" ] || continue
                case "$built" in *" $d "*) ;; *) unmet=1 ;; esac
            done
            if [ -z "$unmet" ]; then
                echo "==> $m"
                "$MILL" machine "$m"
                built="$built$stem "
                progress=1
            else
                remaining="$remaining $m"
            fi
        done
        [ -n "$progress" ] || { echo "include cycle among machines:$remaining" >&2; exit 1; }
        pending=$remaining
    done
}

# Give the extension its own toolchain, so installing the .vsix is the
# whole install: a framework-dependent mill (every RID's natives ride
# along in runtimes/, so one package runs everywhere the .NET runtime
# is) and the machines it needs. The extension points SHODDYLIB at the
# staged machines/, where Include finds the source and the resolver
# finds the DLL beside it in bin/.
stage() {
    machines
    rm -rf "$STAGE_MILL" "$STAGE_LIB"
    # Satellite resource assemblies are Roslyn's localized diagnostics —
    # ~5 MB of the package for messages the mill never surfaces.
    dotnet publish src/Shoddy.Mill -c Release -o "$STAGE_MILL" \
        -p:SatelliteResourceLanguages=en -p:DebugType=none
    mkdir -p "$STAGE_LIB/bin"
    cp machines/*.shoddy "$STAGE_LIB"
    cp machines/bin/*.dll "$STAGE_LIB/bin"
    # Roslyn, Silk.NET, GLFW and OpenAL Soft are redistributed in the
    # package, so their notices have to travel with it — LGPL-2.1 for
    # OpenAL Soft, attribution for the rest.
    cp THIRD-PARTY-NOTICES.md vscode-shoddy
    echo "staged mill + machines into vscode-shoddy/ ($(du -shc "$STAGE_MILL" "$STAGE_LIB" | tail -1 | cut -f1) total)"
}

case "${1:-help}" in
    build)
        build
        ;;
    test)
        dotnet test src/Shoddy.Tests
        ensure_mill
        "$MILL" run tst/libtest.shoddy
        ;;
    run)
        [ $# -ge 2 ] || { echo "usage: ./build.sh run FILE.shoddy" >&2; exit 2; }
        ensure_mill
        "$MILL" run "$2"
        ;;
    weave)
        [ $# -ge 2 ] || { echo "usage: ./build.sh weave FILE.shoddy" >&2; exit 2; }
        ensure_mill
        "$MILL" weave "$2"
        ;;
    machines)
        machines
        ;;
    stage)
        stage
        ;;
    vsix)
        command -v vsce >/dev/null 2>&1 || {
            echo "installing @vscode/vsce (npm -g)..."; npm install -g @vscode/vsce
        }
        stage
        (
            cd vscode-shoddy
            if [ $# -ge 2 ]; then
                vsce package "$2" --no-git-tag-version
            else
                vsce package
            fi
        )
        ;;
    clean)
        rm -rf bin artifacts machines/bin "$STAGE_MILL" "$STAGE_LIB"
        find src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
        echo "cleaned."
        ;;
    help|-h|--help)
        sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
        ;;
    *)
        echo "unknown command: $1" >&2
        echo "run './build.sh help' for usage." >&2
        exit 2
        ;;
esac
