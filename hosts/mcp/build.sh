#!/usr/bin/env bash
# Build the MCP lane: restore, build and test Sparky. The lane's
# projects build the toolchain they weave with and reference the machine
# library, so a plain dotnet build is the whole build; the repo root's
# build runs first only so the mill's own suite below has a mill. The
# .ps1 twin is the file that ships from the Windows workstation; keep
# the two in step.
#
#   ./build.sh              restore + build (Debug)
#   ./build.sh test         build + run Shoddy.Mcp.Tests
#   ./build.sh run          build + start the server on stdin/stdout
#   ./build.sh release      Release build - what a client should launch
#   ./build.sh publish [V]  self-contained single-file binaries, one
#                           archive per OS; V names them (sparky-V-RID)
#                           and is the version every binary reports
#   ./build.sh install [V]  sparky on THIS machine: build, prove, install
#                           to one fixed place, register with Claude Code
#
# install is the one command between a change and an agent using it.
# It publishes the single file for this platform, drives it over stdio
# and refuses if it does not answer (so a broken build never replaces a
# working one), copies it to $SPARKY_HOME (default
# ~/.local/share/shoddy/sparky), and registers that path with Claude Code
# as "sparky", replacing any earlier registration. The path never
# changes, so re-running it is the whole upgrade; a new Claude Code
# session picks it up. By default it registers at project scope, in the
# .mcp.json of the folder install was run from, with --root ".": sparky
# and fettle both root in that repo. An absolute SPARKY_ROOT registers
# at user scope with that root instead - see register(). Without the
# claude CLI it installs and prints the command to register by hand.
#
# run is here for a person holding a terminal, and it will look like it
# has hung: the server is waiting for a JSON-RPC message on stdin, which
# is exactly what it should do. README.md has two lines that prove it is
# alive.
set -euo pipefail
# Where install was run from, before the cd: a project registration is
# written there.
invoked=$PWD
cd "$(dirname "$0")"

# The root's build: the full mill, for the mill's own suite. MSBuild's
# up-to-date check makes this free when nothing changed.
../../build.sh build

# The binary at $1 answers initialize and computes over its woven core,
# or the install stops before anything is replaced.
smoke() {
    local scratch answer
    scratch=$(mktemp -d)
    answer=$(printf '%s\n' \
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}' \
        '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"eval","arguments":{"lines":["{ 12500 13100 11900 } MEAN"]}}}' \
        | "$1" --root "$scratch") || true
    rm -rf "$scratch"
    case "$answer" in
        *'"serverInfo"'*'x: 12500'*) echo "ok  the new sparky answers over stdio" ;;
        *) echo "install: the new sparky did not answer over stdio - nothing was replaced." >&2
           printf '%s\n' "$answer" | head -5 >&2
           exit 1 ;;
    esac
}

# Point Claude Code at $1, replacing an earlier "sparky". An absolute
# SPARKY_ROOT registers at user scope with that root.
#
# OTHERWISE IT REGISTERS AT PROJECT SCOPE, in the .mcp.json of the
# folder install was run from, with --root SPARKY_ROOT (default "."). sparky resolves --root against the
# folder Claude Code launches it in; fettle resolves it against the
# folder holding the registration. Only a project registration makes
# those one folder, so only there does "." mean the repo to both. The
# user-scope "sparky" is removed either way: fettle reads ~/.claude.json
# first, and would pair with that one instead.
register() {
    local root="${SPARKY_ROOT:-.}" scope=project
    case "$root" in
        /*) scope=user; mkdir -p "$root" ;;
        *)  (cd "$invoked" && mkdir -p "$root") ;;
    esac
    local cmd=("$1" --root "$root")
    if command -v claude >/dev/null 2>&1; then
        claude mcp remove --scope user sparky >/dev/null 2>&1 || true
        if [ "$scope" = project ]; then
            (cd "$invoked"
             claude mcp remove --scope project sparky >/dev/null 2>&1 || true
             claude mcp add --scope project sparky -- "${cmd[@]}")
            echo "registered with Claude Code (project scope, $invoked/.mcp.json) as sparky - start a new session there to use it"
        else
            claude mcp add --scope user sparky -- "${cmd[@]}"
            echo "registered with Claude Code (user scope) as sparky - start a new session to use it"
        fi
    else
        echo "the claude CLI is not on PATH, so nothing was registered. Give your MCP client:"
        echo "  ${cmd[*]}"
    fi
}

case "${1:-build}" in
    build)   dotnet build Sparky.slnx ;;
    test)    dotnet test Shoddy.Mcp.Tests ;;
    run)     dotnet build sparky && dotnet run --project sparky --no-build ;;
    # release grades its own closure: no window, audio, toolchain or
    # third-party protocol package rides along (scripts/verify-closure.js).
    release) dotnet build Sparky.slnx -c Release
             node ../../scripts/verify-closure.js sparky ;;
    publish)
        # What a release attaches: a self-contained single-file sparky
        # per OS, so the far end needs no repo and no .NET install.
        # Cross-targeting every RID from one runner is safe because the
        # lane is pure managed code - verify-closure.js asserts that on
        # the release output.
        # The Windows RIDs ship as .zip; the unix RIDs as .tar.gz, because
        # the executable bit only survives a tar. win-arm64 is its own
        # archive so an ARM64 Windows machine runs sparky natively rather
        # than under x64 emulation.
        suffix="${2:+-$2}"
        pub=../../artifacts/publish
        mkdir -p "$pub"
        # ShoddyRidPublish: these restores keep their lock files under
        # artifacts/, unlocked - see Directory.Build.props for why.
        # Single-file is set in sparky/Sparky.csproj, not here.
        for rid in win-x64 win-arm64 linux-x64 osx-x64 osx-arm64; do
            dotnet publish sparky -c Release -r "$rid" --self-contained \
                -p:ShoddyRidPublish=true ${2:+"-p:Version=$2"} \
                -o "$pub/sparky/$rid"
            if [ "${rid#win-}" != "$rid" ]; then
                (cd "$pub/sparky/$rid" && zip -qj "../../sparky$suffix-$rid.zip" sparky.exe)
            else
                chmod +x "$pub/sparky/$rid/sparky"
                tar -czf "$pub/sparky$suffix-$rid.tar.gz" -C "$pub/sparky/$rid" sparky
            fi
        done
        ls -1 "$pub" | sed 's/^/  -> /'
        # Then drive the archive for THIS platform, unpacked outside the
        # repo: the exact file a person downloads, not the build output.
        node ../../scripts/verify-shipped.js sparky "${2:-}" ;;
    install)
        case "$(uname -s)-$(uname -m)" in
            Darwin-arm64)              rid=osx-arm64 ;;
            Darwin-x86_64)             rid=osx-x64 ;;
            Linux-x86_64)              rid=linux-x64 ;;
            Linux-aarch64|Linux-arm64) rid=linux-arm64 ;;
            *) echo "install: no sparky build for $(uname -s) $(uname -m); on Windows run build.ps1 install" >&2
               exit 1 ;;
        esac
        out=../../artifacts/publish/sparky-install/$rid
        dotnet publish sparky -c Release -r "$rid" --self-contained \
            -p:ShoddyRidPublish=true ${2:+"-p:Version=$2"} -o "$out"
        smoke "$out/sparky"
        dest=${SPARKY_HOME:-$HOME/.local/share/shoddy/sparky}
        mkdir -p "$dest"
        # Copied beside, then renamed over: a client running the old one
        # keeps its open file, and the path is never half-written.
        cp "$out/sparky" "$dest/sparky.new"
        chmod +x "$dest/sparky.new"
        mv -f "$dest/sparky.new" "$dest/sparky"
        echo "installed $dest/sparky"
        register "$dest/sparky" ;;
    *)       echo "unknown command: $1 (build | test | run | release | publish | install)" >&2; exit 1 ;;
esac
