#!/usr/bin/env bash
# Shoddy build wrapper (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh build                 build the mill into bin/
#   ./build.sh test                  run the golden suite + libtest assertions
#   ./build.sh run FILE.shoddy       compile in memory and run a program
#   ./build.sh weave FILE.shoddy     compile a program to an assembly
#   ./build.sh machines              compile every machine to a machine DLL
#   ./build.sh vsix [bump]           package the VS Code extension (.vsix)
#   ./build.sh clean                 remove build output
#   ./build.sh help                  show this help
#
# vsix [bump]: optional patch|minor|major or an exact X.Y.Z to bump the
# extension version before packaging (e.g. ./build.sh vsix patch).
set -euo pipefail
cd "$(dirname "$0")"

MILL=bin/mill

build() {
    dotnet publish src/Shoddy.Mill -c Release -o bin
}

ensure_mill() {
    [ -x "$MILL" ] || { echo "mill not built; running build first..."; build; }
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
        ensure_mill
        for m in machines/*.shoddy; do
            echo "==> $m"
            "$MILL" machine "$m"
        done
        ;;
    vsix)
        command -v vsce >/dev/null 2>&1 || {
            echo "installing @vscode/vsce (npm -g)..."; npm install -g @vscode/vsce
        }
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
        rm -rf bin artifacts
        find src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
        echo "cleaned."
        ;;
    help|-h|--help)
        sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
        ;;
    *)
        echo "unknown command: $1" >&2
        echo "run './build.sh help' for usage." >&2
        exit 2
        ;;
esac
