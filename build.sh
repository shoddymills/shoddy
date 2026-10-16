#!/usr/bin/env bash
# Shoddy build wrapper (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh all [bump|X.Y.Z]      everything: clean, test, then .vsix
#   ./build.sh build                 build the mill, with the machine library
#   ./build.sh test                  everything: dotnet tests, the machine
#                                    suites and every mill's own suite
#   ./build.sh check                 the fast gates: docs, errors, permissions,
#                                    host-blind, suites, twins, lanes
#   ./build.sh prove                 THE RELEASE PROOF: everything CI runs
#                                    that this machine can, restored as CI
#                                    restores, and the display suites
#   ./build.sh reproducible          two clean builds must agree byte for byte
#   ./build.sh run [SWITCHES] FILE.shoddy [ARGS...]
#                                    compile in memory and run a program
#   ./build.sh weave FILE.shoddy     compile a program to an assembly
#   ./build.sh machines              build the machine library (same as build)
#   ./build.sh stage [X.Y.Z]         stage the mill into the extension
#   ./build.sh vsix [bump|X.Y.Z]     package the VS Code extension (.vsix)
#   ./build.sh install [X.Y.Z]       install the packaged .vsix into VS Code
#   ./build.sh clean                 remove build output
#   ./build.sh help                  show this help
#
# MSBUILD IS THE BUILD. `build` is `dotnet build src/Shoddy.Mill`: that
# builds the toolchain, builds the machine library in Debug and Release
# (machines/Shoddy.Machines.csproj and the seeds beside it) and carries
# both inside the mill's output under artifacts/, where every other
# project's output lives too. Nothing a build writes lands in a source
# folder, and there is no staleness logic here - MSBuild's up-to-date
# check knows every input, the compiler's own assemblies included. This
# script only names what dotnet cannot do by itself: run the Shoddy
# suites, stage the extension, call vsce and code.
#
# all: the whole toolchain from nothing - clean, then test (which builds
# the mill), then vsix (which stages and packages). This is the path for
# a package you mean to install or ship; `vsix` on its own runs no
# tests, which is fine for a quick turn and not for a release. Takes the
# same optional argument as vsix: ./build.sh all 2.10.0
#
# vsix [bump|X.Y.Z]: patch|minor|major has vsce bump the version in
# package.json, then package. An exact X.Y.Z builds this package as that
# version and changes no file in the tree: the staged mill reports X.Y.Z
# and the package is vscode-shoddy-X.Y.Z.vsix. That is how a local build
# carries a release number (./build.sh vsix 2.10.0).
# vsix stages first, so the package carries its own mill and library.
#
# install [X.Y.Z]: install vscode-shoddy-X.Y.Z.vsix, or with no version,
# the package for the version package.json names.
#
# prove: the one command between finishing work and opening the pull
# request. check, reproducible, test, then each host lane exactly as
# ci.yml drives it (build, test, release, publish - every verb grading
# its own output), then the display suites CI cannot run. Restores run
# LOCKED, as on the runner, so a drifted lock file fails here. What this
# machine cannot run (the MAUI lane off Windows, the display suites with
# no display) is named at the end, never skipped in silence.
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
ROOT=$PWD

# The full toolchain, where `dotnet build` puts it: the apphost beside
# mill.dll, the machine library in lib/ and lib/debug/ beside that, the
# machine sources in machines/.
MILL=artifacts/bin/Shoddy.Mill/release/mill
# A checkout pins the mill to its own machine sources: every mill in
# the tree reaches a machine by relative path (../../machines/x.shoddy),
# and naming the folder is what makes those files machines rather than
# source to splice. The extension does the same for the sources it
# carries.
export SHODDYLIB="$PWD/machines"
# Where `stage` publishes the extension's own copy of the toolchain.
# Build output: gitignored, and rebuilt from scratch on every stage.
STAGE_MILL=vscode-shoddy/mill

build() {
    dotnet build src/Shoddy.Mill -c Release
}

# Two clean builds of one commit must agree byte for byte: the
# deterministic settings in Directory.Build.props are proved, not
# assumed. ci.yml's core job calls this verb; it is the only copy.
reproducible() {
    sums() {
        ( cd artifacts/bin/Shoddy.Mill/release
          if command -v sha256sum >/dev/null 2>&1; then sha256sum ./*.dll lib/*.dll
          else shasum -a 256 ./*.dll lib/*.dll; fi )
    }
    rm -rf artifacts; build; first=$(sums)
    rm -rf artifacts; build; second=$(sums)
    if [ "$first" != "$second" ]; then
        echo "the build is not reproducible - two clean builds differ:" >&2
        diff <(printf '%s\n' "$first") <(printf '%s\n' "$second") >&2 || true
        exit 1
    fi
    echo "reproducible: two clean builds agree byte for byte"
}

# The machine suites. Every machines/<name>.shoddy that has one is
# graded here, by name rather than by glob: a suite that stops being
# listed is a suite that stops running, and that is how nine of them
# once stopped running unnoticed. The list itself lives in
# scripts/suites.mjs - THE ONLY COPY. This script and build.ps1
# both read it through node, after three hand-kept copies drifted
# apart and dropped seedterminaltest between them;
# scripts/verify-suites.js (build check, and CI) proves the roster
# complete.
# isamdump runs after isamtest and takes DELETE, because it reopens
# the file isamtest leaves behind - the round trip across two
# processes is the thing being proved, and DELETE clears up after it.
machine_suites() {
    suites=$(node --input-type=module -e "const m = await import('./scripts/suites.mjs'); console.log(m.MACHINE_SUITES.join(' '))") \
        || { echo "could not read MACHINE_SUITES from scripts/suites.mjs" >&2; exit 1; }
    for suite in $suites; do
        echo "==> tst/$suite.shoddy"
        "$MILL" run "tst/$suite.shoddy" </dev/null
    done
    # seedscribblertest, seedturtletest and seedplottertest are NOT run
    # here. --no-window hides the window, but it is still a real one, and
    # GLFW has no platform to open it on on GitHub's hosted runners - no
    # X server's worth of libraries even under Xvfb (v1.10.1 shipped that
    # gate and it still failed the same way). Run them by hand, locally,
    # on a machine with a real display: scripts/shoddy-display.sh.
    # isamtest leaves _isamtest.tmp and its index for isamdump to reopen,
    # and both are written where the program runs: a scratch folder, so
    # nothing a suite writes lands in the tree.
    scratch=$(mktemp -d)
    echo "==> tst/isamtest.shoddy"
    ( cd "$scratch" && "$ROOT/$MILL" run "$ROOT/tst/isamtest.shoddy" </dev/null )
    echo "==> tst/isamdump.shoddy"
    ( cd "$scratch" && "$ROOT/$MILL" run "$ROOT/tst/isamdump.shoddy" DELETE </dev/null )
    rm -rf "$scratch"
}

# Every mill's own test target, through the mill's own build.sh, so
# there is one statement of how a mill is tested and it lives with the
# mill. Every directory under mills/ is visited: a mill with no test
# target fails here with its usage message, which is the intended
# answer to shipping one without a suite.
mill_suites() {
    for m in mills/*/; do
        echo "==> ${m}build.sh test"
        ( cd "$m" && ./build.sh test </dev/null )
    done
}

# stage [X.Y.Z]: publish the full mill into the extension, framework-
# dependent (every RID's natives ride along in runtimes/, so one package
# runs everywhere the .NET runtime is), carrying the machine library in
# lib/ and the machine sources in machines/. With a version, the staged
# mill and every assembly published with it carry that version; it is
# passed to this one publish as a property rather than written into
# Directory.Build.props, so the tree is left as it was.
stage() {
    version=${1:-}
    rm -rf "$STAGE_MILL" vscode-shoddy/machines
    # Satellite resource assemblies are Roslyn's localized diagnostics -
    # ~5 MB of the package for messages the mill never surfaces.
    dotnet publish src/Shoddy.Mill -c Release -o "$STAGE_MILL" \
        -p:SatelliteResourceLanguages=en \
        ${version:+"-p:Version=$version"}
    # Roslyn, Silk.NET, GLFW and OpenAL Soft are redistributed in the
    # package, so their notices have to travel with it - LGPL-2.1 for
    # OpenAL Soft, attribution for the rest. The authorship statement and
    # its AI disclosure travel with it for the same reason: the .vsix is
    # the whole install for most people, and never sees the repository.
    cp THIRD-PARTY-NOTICES.md vscode-shoddy
    cp AUTHORSHIP.md vscode-shoddy
    echo "staged the mill and its library into vscode-shoddy/ ($(du -sh "$STAGE_MILL" | cut -f1))"
}

# An exact release number, as release.yml accepts it from a tag.
is_version() {
    [[ $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
}

# Package the extension as version $1 without changing package.json. vsce
# names and labels the package from package.json, so the version has to be
# in that file while vsce runs. The file is copied aside first and moved
# back on every way out: success, failure, Ctrl+C. A copy left behind
# means a run was killed outright, and package.json may still carry the
# stamp, so the next run stops and says how to put it back rather than
# guessing which of the two files is the real one.
# Runs in vscode-shoddy/.
package_as() {
    kept=../artifacts/package.json.unstamped
    if [ -e "$kept" ]; then
        echo "$kept is left from an interrupted vsix run." >&2
        echo "Check it, then put it back: mv artifacts/package.json.unstamped vscode-shoddy/package.json" >&2
        exit 1
    fi
    mkdir -p ../artifacts
    cp -p package.json "$kept"
    trap 'mv -f "$kept" package.json' EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    node -e "const f=require('fs');const p=JSON.parse(f.readFileSync('package.json','utf8'));p.version=process.argv[1];f.writeFileSync('package.json',JSON.stringify(p,null,2)+'\n')" "$1"
    vsce package
}

case "${1:-help}" in
    all)
        # Re-invoked rather than inlined so each step stays exactly the
        # command it documents - no second copy of clean/test/vsix to drift
        # out of step with the real ones. set -e stops the chain on the
        # first failure, so a red test never reaches the packager.
        "$SELF" clean
        "$SELF" test
        "$SELF" vsix ${2:+"$2"}
        ;;
    build|machines)
        build
        ;;
    test)
        build
        # The C# suite: the toolchain's own tests, the proof projects it
        # builds and runs, and the debugger driven as the editor drives
        # it. The test host references the full mill, so the library
        # and its sources travel into its output and in-process tests
        # resolve them the way the mill does.
        # The results go under artifacts/ as a .trx, the standard form:
        # the whole of a failure's output is there when the console has
        # scrolled it away, and CI can attach it.
        dotnet test src/Shoddy.Tests --logger "trx;LogFileName=Shoddy.Tests.trx" \
            --results-directory artifacts/test-results
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
        # because the network is a gated capability - without the flag the
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
        # that no longer compiles. It asserts nothing - tst/sinq.shoddy
        # grades the behaviour; this proves the example still READS as
        # documented.
        "$MILL" run tst/sinq-demo.shoddy
        # Everything else the tree can prove about itself: a suite for
        # every machine that has one, and every mill's own suite. Nothing
        # is released that has not been through this.
        machine_suites
        mill_suites
        # The rest of CI's core job, so this target IS that job: every
        # mill.manifest describes its mill, and the headless mill still
        # weaves and refuses `run` by name (ci.yml's container job proves
        # the same with no X11 and no audio device at all).
        node scripts/verify-manifests.js
        node scripts/verify-headless.js
        ;;
    reproducible)
        reproducible
        ;;
    prove)
        export SHODDY_LOCKED_RESTORE=1
        step() { printf '\n=== %s\n' "$1"; }
        proved="check, reproducible, test, mcp lane"
        skipped=()
        step "check: the fast gates";        "$SELF" check
        step "reproducible: two clean builds"; "$SELF" reproducible
        step "test: the whole suite";        "$SELF" test
        step "mcp lane: build, test, release, publish"
        ( cd hosts/mcp && ./build.sh build && ./build.sh test \
                       && ./build.sh release && ./build.sh publish )
        case "$(uname -s)" in
            MINGW*|MSYS*|CYGWIN*)
                step "maui lane: build, test, release"
                ( cd hosts/maui && ./build.sh build && ./build.sh test && ./build.sh release )
                proved="$proved, maui lane" ;;
            *)  skipped+=("maui lane - it builds on Windows only; CI's windows runner proves it") ;;
        esac
        if [ "$(uname -s)" = "Darwin" ] || [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
            step "display suites: the three CI cannot run"
            scripts/shoddy-display.sh
            proved="$proved, display suites"
        else
            skipped+=("display suites - no display here, and CI has none: run scripts/shoddy-display.sh where there is one")
        fi
        echo
        echo "PROVED: $proved"
        # The ${a[@]+...} form, because macOS ships bash 3.2, where an
        # empty array under set -u is an unbound variable.
        for s in ${skipped[@]+"${skipped[@]}"}; do
            echo "NOT PROVED ON THIS MACHINE: $s"
        done
        ;;
    check)
        # The fast gates - the doc and boundary verifiers. All Node, all
        # quick, and the same list CI's checks job runs.
        for gate in verify-docs verify-errors verify-permissions \
                    verify-host-blind verify-suites verify-twins verify-lanes; do
            echo "==> scripts/$gate.js"
            node "scripts/$gate.js"
        done
        # The VS Code extension's own logic (the debug launch), tested
        # with no editor running.
        echo "==> vscode-shoddy/test"
        node --test vscode-shoddy/test/launch.test.js
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
        build
        # The ${a[@]+...} form, because macOS still ships bash 3.2, where
        # an empty array under set -u is an unbound variable.
        "$MILL" run ${switches[@]+"${switches[@]}"} "$@"
        ;;
    weave)
        [ $# -ge 2 ] || { echo "usage: ./build.sh weave FILE.shoddy" >&2; exit 2; }
        build
        "$MILL" weave "$2"
        ;;
    stage)
        stage ${2:+"$2"}
        ;;
    vsix)
        # vsce is the ONE target with a dependency outside this repo's own
        # tools, and it is unavoidable: a .vsix is a VS Code extension
        # package and there is no dotnet or Shoddy path to producing one.
        # Every other target here - and every mill's own wrapper - needs
        # nothing but this shell, dotnet and node.
        #
        # It is REPORTED rather than installed. This used to run
        # `npm install -g @vscode/vsce` when vsce was missing, which is a
        # build script putting software on your machine, globally, without
        # asking. CI does not need that behaviour either: release.yml
        # installs vsce as its own explicit step before it calls this.
        command -v vsce >/dev/null 2>&1 || {
            echo "vsix needs @vscode/vsce, which is not installed." >&2
            echo "  npm install -g @vscode/vsce      (needs Node.js)" >&2
            echo "Every other target - build, test, run, weave, stage," >&2
            echo "clean - needs only this shell, dotnet and node." >&2
            exit 1
        }
        if [ $# -ge 2 ] && is_version "$2"; then
            stage "$2"
            (
                cd vscode-shoddy
                package_as "$2"
            )
        else
            stage
            (
                cd vscode-shoddy
                if [ $# -ge 2 ]; then
                    vsce package "$2" --no-git-tag-version
                else
                    vsce package
                fi
            )
        fi
        ;;
    install)
        # The other target with a dependency outside the repo's tools -
        # the `code` CLI - and, like vsce, it is REPORTED rather than
        # acquired. It installs the .vsix for the version given, or else
        # the version package.json currently names, and refuses if that
        # exact package has not been built: installing whatever .vsix
        # happens to be newest is how a stale extension gets mistaken for
        # a fresh one, which is the failure this target exists to end.
        #
        # Deliberately NOT part of `all`: release.yml runs `all` on a
        # runner with no VS Code, and installing into an editor is a
        # workstation act, not a build step. Locally the pair is:
        # ./build.sh all, then install.
        command -v code >/dev/null 2>&1 || {
            echo "install needs the VS Code CLI (code), which is not on PATH." >&2
            exit 1
        }
        if [ $# -ge 2 ]; then
            is_version "$2" || { echo "usage: ./build.sh install [X.Y.Z]" >&2; exit 2; }
            ver=$2
        else
            ver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' vscode-shoddy/package.json | head -1)
        fi
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
        # Every MSBuild output lives under artifacts/, and the stage is
        # the one other place a build writes. Anything else git ignores -
        # a packaged .vsix, a log - is yours: `git clean -Xdf` removes it
        # all, and asks nothing.
        rm -rf artifacts "$STAGE_MILL" vscode-shoddy/machines
        echo "cleaned."
        ;;
    help|-h|--help)
        # The whole header comment, however long it grows.
        awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "$SELF"
        ;;
    *)
        echo "unknown command: $1" >&2
        echo "run './build.sh help' for usage." >&2
        exit 2
        ;;
esac
