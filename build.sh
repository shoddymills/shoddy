#!/usr/bin/env bash
# Shoddy build wrapper (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh all [bump]            everything: clean, test, then .vsix
#   ./build.sh build                 build the mill into bin/
#   ./build.sh test                  everything: dotnet tests, the machine
#                                    suites and every mill's own suite
#   ./build.sh check                 the fast gates: docs, errors, permissions,
#                                    host-blind, suites, twins, lanes
#   ./build.sh run [SWITCHES] FILE.shoddy [ARGS...]
#                                    compile in memory and run a program
#   ./build.sh weave FILE.shoddy     compile a program to an assembly
#   ./build.sh machines              compile every machine to a machine DLL
#   ./build.sh stage                 stage the mill + machines into the extension
#   ./build.sh vsix [bump]           package the VS Code extension (.vsix)
#   ./build.sh install               install the packaged .vsix into VS Code
#   ./build.sh clean                 remove build output
#   ./build.sh help                  show this help
#
# all: the whole toolchain from nothing — clean, then test (which rebuilds
# the mill), then vsix (which builds the machines, stages, and packages).
# This is the path for a package you mean to install or ship; `vsix` on its
# own runs no tests, which is fine for a quick turn and not for a release.
# Takes the same optional bump as vsix: ./build.sh all patch
#
# Every target that runs the mill publishes it first, every time - see
# build() below for why that is not left to a check.
#
# vsix [bump]: optional patch|minor|major or an exact X.Y.Z to bump the
# extension version before packaging (e.g. ./build.sh vsix patch).
# vsix stages first, so the package carries its own mill and machines.
#
# run [SWITCHES] FILE.shoddy [ARGS...]: SWITCHES go before the file and
# are the mill's own - --allow-net (arm the network), --no-window (hide
# scribbler windows), --no-lint, --lint-verbose. ARGS after the file reach
# the program through Args, e.g.
#   ./build.sh run --allow-net mills/weather-glass/weather-glass.shoddy 63011
set -euo pipefail
cd "$(dirname "$0")"

# Absolute path to this script, for `all` to re-invoke. Resolved after the
# cd above: a relative $0 ("sub/build.sh") would no longer point at anything
# once the working directory has moved.
SELF="$PWD/$(basename "$0")"

MILL=bin/mill
# Where `stage` drops the extension's own copy of the toolchain. Both are
# build output: gitignored, and rebuilt from scratch on every stage.
STAGE_MILL=vscode-shoddy/mill
STAGE_LIB=vscode-shoddy/machines

# THE MILL IS REBUILT ON EVERY CALL, NOT WHEN IT LOOKS MISSING. This was
# `[ -x bin/mill ] || build`, which never rebuilt a mill that existed. A
# mill from before a compiler change then went on weaving every machine
# with the old compiler - the lists rework moved Taken from seq into the
# runtime, the old mill still compiled it as a seq word, and every sparky
# session failed with "unknown word: TAKEN" while each step reported
# success. Deciding staleness here would mean keeping a list of what the
# mill is built from; MSBuild already knows exactly.
#
# AND IT IS PUBLISHED INTO AN EMPTY FOLDER, NEVER STRAIGHT INTO bin/.
# Publish copies a file only when the source is NEWER than the one already
# there, so a mill in bin/ with later timestamps than the build output - one
# published from another checkout, or restored from anywhere - survives a
# `dotnet publish -o bin` untouched while the publish reports success. That
# was proved, not supposed: an older mill planted in bin/ was still the one
# running after the publish. So the build lands in $MILL_PUB, which is
# emptied first, and bin/ is replaced by it whenever the two differ by a
# single byte. About a second when nothing changed.
#
# THEN THE MACHINE DLLS ARE CHECKED AGAINST THE MILL THAT WOVE THEM. A
# fresh mill is not enough on its own: the resolver judges a machine DLL
# only against its source and its dependencies, so DLLs woven by the old
# mill still pass as current. Each machine bin/ carries .mill-id, the hash
# of the mill's DLLs at the time it was emptied; when that differs from
# the mill now in bin/, the folder is emptied and the resolver weaves
# every machine afresh with this mill on first use. A hash of the content,
# not a timestamp: a timestamp moves when nothing changed (another project
# recompiling Shoddy.Runtime with its own settings does it) and can stay
# put when something did. A folder with no .mill-id is treated as
# mismatched, so a tree from before this rule is emptied once.
MILL_PUB=artifacts/mill-publish
build() {
    rm -rf "$MILL_PUB"
    dotnet publish src/Shoddy.Mill -c Release -o "$MILL_PUB"
    if diff -r -q "$MILL_PUB" bin >/dev/null 2>&1; then
        rm -rf "$MILL_PUB"
    else
        [ -d bin ] && echo "bin/ is not the mill just built: replacing it"
        rm -rf bin
        mv "$MILL_PUB" bin
    fi
    id=$(mill_id)
    for d in machines/bin machines/seeds/bin; do
        if [ "$(cat "$d/.mill-id" 2>/dev/null)" != "$id" ]; then
            [ -d "$d" ] && echo "mill changed: emptying $d so its machines are rewoven by this mill"
            rm -rf "$d"
            mkdir -p "$d"
            echo "$id" > "$d/.mill-id"
        fi
    done
}

# The identity of the mill in bin/: one hash over all of its DLLs, taken
# in byte order of their names (LC_ALL=C, so the locale cannot reorder
# them). build.ps1's Get-MillId reads the same bytes in the same order.
# sha256sum is on Linux and current macOS; shasum covers older Macs.
mill_id() {
    (
        LC_ALL=C
        if command -v sha256sum >/dev/null 2>&1; then
            cat bin/*.dll | sha256sum | cut -d' ' -f1
        else
            cat bin/*.dll | shasum -a 256 | cut -d' ' -f1
        fi
    )
}

ensure_mill() {
    build
}

machines() {
    ensure_mill
    # Dependency order: an Include "x.shoddy" resolves to the machine DLL
    # only if Shoddy.Machines.X.dll is already built — otherwise the
    # source is spliced in and its defs re-exported, which collides with
    # the real machine downstream (duplicate definition of ANY, etc.).
    pending=$(echo machines/*.shoddy machines/seeds/*.shoddy)
    built=" "
    while [ -n "$pending" ]; do
        progress=
        remaining=
        for m in $pending; do
            stem=$(basename "$m" .shoddy | tr 'A-Z' 'a-z')
            unmet=
            for d in $(sed -n 's/^[[:space:]]*Include[[:space:]]*"\(.*\)\.shoddy".*/\1/p' "$m" | tr 'A-Z' 'a-z'); do
                [ -f "machines/$d.shoddy" ] || [ -f "machines/seeds/$d.shoddy" ] || continue
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
# The machine suites. Every machines/<name>.shoddy that has one is
# graded here, by name rather than by glob: a suite that stops being
# listed is a suite that stops running, and that is how nine of them
# once stopped running unnoticed. The list itself lives in
# scripts/suites.mjs — THE ONLY COPY. This script and build.ps1
# both read it through node, after three hand-kept copies drifted
# apart and dropped seedterminaltest between them;
# scripts/verify-suites.js (build check, and CI) proves the roster
# complete.
# isamdump runs after isamtest and takes DELETE, because it reopens
# the file isamtest leaves behind — the round trip across two
# processes is the thing being proved, and DELETE clears up after it.
machine_suites() {
    ensure_mill
    suites=$(node --input-type=module -e "const m = await import('./scripts/suites.mjs'); console.log(m.MACHINE_SUITES.join(' '))") \
        || { echo "could not read MACHINE_SUITES from scripts/suites.mjs" >&2; exit 1; }
    for suite in $suites; do
        echo "==> tst/$suite.shoddy"
        "$MILL" run "tst/$suite.shoddy" </dev/null
    done
    # seedscribblertest, seedturtletest and seedplottertest are NOT run
    # here. --no-window hides the window, but it is still a real one, and
    # GLFW has no platform to open it on on GitHub's hosted runners — no
    # X server's worth of libraries even under Xvfb (v1.10.1 shipped that
    # gate and it still failed the same way). Run them by hand, locally,
    # on a machine with a real display: `bin/mill --no-window run
    # tst/seedscribblertest.shoddy` and the same for turtle/plotter.
    echo "==> tst/isamtest.shoddy"
    "$MILL" run tst/isamtest.shoddy </dev/null
    echo "==> tst/isamdump.shoddy"
    "$MILL" run tst/isamdump.shoddy DELETE </dev/null
}

# Every mill's own test target, through the mill's own build.sh, so
# there is one statement of how a mill is tested and it lives with the
# mill. Every directory under mills/ is visited: a mill with no test
# target fails here with its usage message, which is the intended
# answer to shipping one without a suite.
mill_suites() {
    ensure_mill
    for m in mills/*/; do
        echo "==> ${m}build.sh test"
        ( cd "$m" && ./build.sh test </dev/null )
    done
}

stage() {
    machines
    rm -rf "$STAGE_MILL" "$STAGE_LIB"
    # Satellite resource assemblies are Roslyn's localized diagnostics —
    # ~5 MB of the package for messages the mill never surfaces.
    dotnet publish src/Shoddy.Mill -c Release -o "$STAGE_MILL" \
        -p:SatelliteResourceLanguages=en -p:DebugType=none
    mkdir -p "$STAGE_LIB/bin" "$STAGE_LIB/seeds/bin"
    cp machines/*.shoddy "$STAGE_LIB"
    cp machines/bin/*.dll "$STAGE_LIB/bin"
    cp machines/seeds/*.shoddy "$STAGE_LIB/seeds"
    cp machines/seeds/bin/*.dll "$STAGE_LIB/seeds/bin"
    # One timestamp across every staged DLL, newer than every staged
    # source. cp stamps them in the order it copies — alphabetical — and
    # a machine is stale when a DLL it depends on is newer than its own,
    # so csv (depending on dict, seq and str, all later in the alphabet)
    # arrived looking stale and rebuilt itself on first use, inside the
    # installed extension. Levelling the stamps says what is true: these
    # were all built together.
    # One reference stamp, not `touch` per file: touch takes the time at
    # each file, and those differ by a tick in the same alphabetical
    # order, which is all the staleness test compares.
    : > "$STAGE_LIB/bin/.stamp"
    touch -r "$STAGE_LIB/bin/.stamp" "$STAGE_LIB"/bin/*.dll "$STAGE_LIB"/seeds/bin/*.dll
    rm -f "$STAGE_LIB/bin/.stamp"
    # Roslyn, Silk.NET, GLFW and OpenAL Soft are redistributed in the
    # package, so their notices have to travel with it — LGPL-2.1 for
    # OpenAL Soft, attribution for the rest. The authorship statement and
    # its AI disclosure travel with it for the same reason: the .vsix is
    # the whole install for most people, and never sees the repository.
    cp THIRD-PARTY-NOTICES.md vscode-shoddy
    cp AUTHORSHIP.md vscode-shoddy
    echo "staged mill + machines into vscode-shoddy/ ($(du -shc "$STAGE_MILL" "$STAGE_LIB" | tail -1 | cut -f1) total)"
}

case "${1:-help}" in
    all)
        # Re-invoked rather than inlined so each step stays exactly the
        # command it documents — no second copy of clean/test/vsix to drift
        # out of step with the real ones. set -e stops the chain on the
        # first failure, so a red test never reaches the packager.
        "$SELF" clean
        "$SELF" test
        "$SELF" vsix ${2:+"$2"}
        ;;
    build)
        build
        ;;
    test)
        # The mill first: ProofTests can publish it themselves when it is
        # missing, but that fallback runs inside `dotnet test` on a cold
        # cache - it stalled a clean release runner for half an hour
        # (v2.0.0). Building it here makes the fallback a no-op.
        ensure_mill
        dotnet test src/Shoddy.Tests
        # The core suites: the golden library suite, the builtin surface
        # check, and the numerics/property suites. The roster is
        # CORE_SUITES in scripts/suites.mjs - THE ONLY COPY, shared with
        # build.ps1 and proved complete by scripts/verify-suites.js - and
        # the reasoning for each suite is written beside its name there.
        core=$(node --input-type=module -e "const m = await import('./scripts/suites.mjs'); console.log(m.CORE_SUITES.join(' '))") \
            || { echo "could not read CORE_SUITES from scripts/suites.mjs" >&2; exit 1; }
        for suite in $core; do
            echo "==> tst/$suite.shoddy"
            "$MILL" run "tst/$suite.shoddy" </dev/null
        done
        # The net demo is the only end-to-end exercise of the socket words:
        # it stands up a server, connects a client to it and trades lines,
        # both ends in one process on loopback. --allow-net is required
        # because the network is a gated capability — without the flag the
        # first socket word aborts, so this run also proves the gate opens.
        "$MILL" run --allow-net tst/net-demo.shoddy
        # The tutorial's program is documentation that has to keep
        # working: its pure half is headless by design, so the checks run
        # here beside everything else and the tutorial cannot drift from
        # code that no longer compiles.
        "$MILL" run tutorials/spiro/test.shoddy
        # The sinq demo is the same argument for a machine rather than a
        # tutorial: docs/machines/sinq.html reproduces it line for line,
        # so running it here is what stops the page showing a program
        # that no longer compiles. It asserts nothing — tst/sinq.shoddy
        # grades the behaviour; this proves the example still READS as
        # documented.
        "$MILL" run tst/sinq-demo.shoddy
        # Everything else the tree can prove about itself: a suite for
        # every machine that has one, and every mill's own suite. Both
        # existed before and neither ran here, which meant a release
        # could ship a broken machine or a broken mill through a green
        # gate. Nothing is released that has not been through this.
        machine_suites
        mill_suites
        ;;
    check)
        # The fast gates - the doc and boundary verifiers. All Node, all
        # quick, and the same list CI's checks job runs.
        for gate in verify-docs verify-errors verify-permissions \
                    verify-host-blind verify-suites verify-twins verify-lanes; do
            echo "==> scripts/$gate.js"
            node "scripts/$gate.js"
        done
        ;;
    run)
        # Switches before the file are the mill's own and pass through
        # as-is; everything after the file is the program's, reaching it
        # through Args (weather-glass takes its ZIP there). An unknown
        # switch is refused here rather than handed on, where the mill
        # would take it for the file name.
        usage="usage: ./build.sh run [--allow-net] [--no-window] [--no-lint] [--lint-verbose] FILE.shoddy [ARGS...]"
        shift
        switches=()
        while [ $# -gt 0 ]; do
            case "$1" in
                --allow-net|--no-window|--no-lint|--lint-verbose) switches+=("$1"); shift ;;
                --*) echo "unknown run switch: $1" >&2; echo "$usage" >&2; exit 2 ;;
                *) break ;;
            esac
        done
        [ $# -ge 1 ] || { echo "$usage" >&2; exit 2; }
        ensure_mill
        # The ${a[@]+...} form, because macOS still ships bash 3.2, where
        # an empty array under set -u is an unbound variable.
        "$MILL" run ${switches[@]+"${switches[@]}"} "$@"
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
        # vsce is the ONE target with a dependency outside this repo's own
        # tools, and it is unavoidable: a .vsix is a VS Code extension
        # package and there is no dotnet or Shoddy path to producing one.
        # Every other target here — and every mill's own wrapper — needs
        # nothing but this shell, dotnet, and bin/mill.
        #
        # It is REPORTED rather than installed. This used to run
        # `npm install -g @vscode/vsce` when vsce was missing, which is a
        # build script putting software on your machine, globally, without
        # asking. CI does not need that behaviour either: release.yml
        # installs vsce as its own explicit step before it calls this.
        command -v vsce >/dev/null 2>&1 || {
            echo "vsix needs @vscode/vsce, which is not installed." >&2
            echo "  npm install -g @vscode/vsce      (needs Node.js)" >&2
            echo "Every other target — build, test, run, weave, machines," >&2
            echo "stage, clean — needs only this shell and dotnet." >&2
            exit 1
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
    install)
        # The other target with a dependency outside the repo's tools —
        # the `code` CLI — and, like vsce, it is REPORTED rather than
        # acquired. It installs the .vsix for the version package.json
        # currently names, and refuses if that exact package has not
        # been built: installing whatever .vsix happens to be newest is
        # how a stale extension gets mistaken for a fresh one, which is
        # the failure this target exists to end.
        #
        # Deliberately NOT part of `all`: release.yml runs `all` on a
        # runner with no VS Code, and installing into an editor is a
        # workstation act, not a build step. Locally the pair is:
        # ./build.sh all, then install.
        command -v code >/dev/null 2>&1 || {
            echo "install needs the VS Code CLI (code), which is not on PATH." >&2
            exit 1
        }
        ver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' vscode-shoddy/package.json | head -1)
        pkg="vscode-shoddy/vscode-shoddy-$ver.vsix"
        [ -f "$pkg" ] || {
            echo "no package for the current version: $pkg is not there." >&2
            echo "run ./build.sh vsix (or all) first - install does not guess at a stale package." >&2
            exit 1
        }
        code --install-extension "$pkg" --force
        echo "installed $pkg - reload VS Code windows to pick it up."
        ;;
    clean)
        rm -rf bin artifacts machines/bin machines/seeds/bin "$STAGE_MILL" "$STAGE_LIB"
        find src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
        echo "cleaned."
        ;;
    help|-h|--help)
        sed -n '2,38p' "$SELF" | sed 's/^# \{0,1\}//'
        ;;
    *)
        echo "unknown command: $1" >&2
        echo "run './build.sh help' for usage." >&2
        exit 2
        ;;
esac
