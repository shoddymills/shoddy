#!/usr/bin/env pwsh
# Shoddy build wrapper (Windows). Unix users: use build.sh (same commands).
#
#   ./build.ps1 all [bump|X.Y.Z]      everything: clean, test, then .vsix
#   ./build.ps1 build                 build the mill, with the machine library
#   ./build.ps1 test                  everything: dotnet tests, the machine
#                                     suites and every mill's own suite
#   ./build.ps1 check                 the fast gates: docs, errors, permissions,
#                                     host-blind, suites, twins, lanes
#   ./build.ps1 run [SWITCHES] FILE.shoddy [ARGS...]
#                                     compile in memory and run a program
#   ./build.ps1 weave FILE.shoddy     compile a program to an assembly
#   ./build.ps1 machines              build the machine library (same as build)
#   ./build.ps1 stage [X.Y.Z]         stage the mill into the extension
#   ./build.ps1 vsix [bump|X.Y.Z]     package the VS Code extension (.vsix)
#   ./build.ps1 install [X.Y.Z]       install the packaged .vsix into VS Code
#   ./build.ps1 clean                 remove build output
#   ./build.ps1 help                  show this help
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
# same optional argument as vsix: ./build.ps1 all 2.10.0
#
# vsix [bump|X.Y.Z]: patch|minor|major has vsce bump the version in
# package.json, then package. An exact X.Y.Z builds this package as that
# version and changes no file in the tree: the staged mill reports X.Y.Z
# and the package is vscode-shoddy-X.Y.Z.vsix. That is how a local build
# carries a release number (./build.ps1 vsix 2.10.0).
# vsix stages first, so the package carries its own mill and library.
#
# install [X.Y.Z]: install vscode-shoddy-X.Y.Z.vsix, or with no version,
# the package for the version package.json names.
#
# run [SWITCHES] FILE.shoddy [ARGS...]: SWITCHES go before the file and
# are the mill's own - --allow-net (arm the network), --no-window (hide
# scribbler windows), --no-lint, --lint-verbose. ARGS after the file reach
# the program through Args, e.g.
#   ./build.ps1 run --allow-net mills/weather-glass/weather-glass.shoddy 63011
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$File,
    # Everything after $File - only `run` reads it, as the rest of its
    # switches, file and program arguments.
    [Parameter(Position = 2, ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Run a native program with $ErrorActionPreference neutralised for its
# duration, and judge it on its EXIT CODE alone - the only honest signal a
# native program gives.
#
# Windows PowerShell 5.1 wraps every line a native program writes to stderr
# in a NativeCommandError record whenever the CALLER captures our streams:
# `./build.ps1 all 2>&1 | tee log.txt`, a CI step, an assistant's shell.
# Under 'Stop' that record is terminating, so an ordinary informational
# line kills the run even though the program exited 0.
#
# 'Stop' stays in force everywhere else, because it is doing real work for
# CMDLET failures: a Copy-Item that fails during stage must not go on to
# package a broken .vsix.
function Native {
    $exe = $args[0]
    $rest = @($args[1..($args.Count - 1)])
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $exe @rest } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# The full toolchain, where `dotnet build` puts it: the apphost beside
# mill.dll, the machine library in lib/ and lib/debug/ beside that, the
# machine sources in machines/.
$Mill = Join-Path 'artifacts' (Join-Path 'bin' (Join-Path 'Shoddy.Mill' (Join-Path 'release' 'mill.exe')))
# A checkout pins the mill to its own machine sources: every mill in
# the tree reaches a machine by relative path (../../machines/x.shoddy),
# and naming the folder is what makes those files machines rather than
# source to splice. The extension does the same for the sources it
# carries.
$env:SHODDYLIB = Join-Path $PSScriptRoot 'machines'
# Where `stage` publishes the extension's own copy of the toolchain.
# Build output: gitignored, and rebuilt from scratch on every stage.
$StageMill = Join-Path 'vscode-shoddy' 'mill'

function Invoke-Build {
    Native dotnet build src/Shoddy.Mill -c Release
}

# Read a list out of scripts/suites.mjs - THE ONLY COPY of the suite
# roster, shared with build.sh and proved complete by verify-suites.js.
function Read-Roster([string]$Name) {
    $code = "const m = await import('./scripts/suites.mjs'); console.log(m.$Name.join(' '))"
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $names = (& node --input-type=module -e $code) } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine("could not read $Name from scripts/suites.mjs"); exit 1 }
    $out = @(($names | Out-String).Trim() -split '\s+' | Where-Object { $_ })
    if ($out.Count -lt 1) { [Console]::Error.WriteLine("$Name in scripts/suites.mjs is empty"); exit 1 }
    return $out
}

# stage [X.Y.Z]: publish the full mill into the extension, framework-
# dependent (every RID's natives ride along in runtimes/, so one package
# runs everywhere the .NET runtime is), carrying the machine library in
# lib/ and the machine sources in machines/. With a version, the staged
# mill and every assembly published with it carry that version; it is
# passed to this one publish as a property rather than written into
# Directory.Build.props, so the tree is left as it was.
function Invoke-Stage {
    param([string]$Version)
    foreach ($d in $StageMill, (Join-Path 'vscode-shoddy' 'machines')) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    # Satellite resource assemblies are Roslyn's localized diagnostics -
    # ~5 MB of the package for messages the mill never surfaces.
    #
    # THE QUOTES ROUND THE -p: ARGUMENTS ARE LOAD-BEARING. PowerShell reads
    # a bare -name:value as a named parameter with a colon-bound argument,
    # and Native takes everything through $args with no param() block, so
    # the name is discarded and only the value survives. MSBuild then sees
    # SatelliteResourceLanguages=en as a second project to build and stops
    # with MSB1008. Quoting is correct in both shells.
    if ($Version) {
        Native dotnet publish src/Shoddy.Mill -c Release -o $StageMill `
            '-p:SatelliteResourceLanguages=en' "-p:Version=$Version"
    }
    else {
        Native dotnet publish src/Shoddy.Mill -c Release -o $StageMill `
            '-p:SatelliteResourceLanguages=en'
    }
    # Roslyn, Silk.NET, GLFW and OpenAL Soft are redistributed in the
    # package, so their notices have to travel with it - LGPL-2.1 for
    # OpenAL Soft, attribution for the rest. The authorship statement and
    # its AI disclosure travel with it for the same reason: the .vsix is
    # the whole install for most people, and never sees the repository.
    Copy-Item THIRD-PARTY-NOTICES.md vscode-shoddy
    Copy-Item AUTHORSHIP.md vscode-shoddy
    $size = (Get-ChildItem $StageMill -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ('staged the mill and its library into vscode-shoddy/ ({0:N1} MB)' -f ($size / 1MB))
}

function Invoke-Test {
    Invoke-Build
    # The C# suite: the toolchain's own tests, the proof projects it
    # builds and runs, and the debugger driven as the editor drives it.
    # The results go under artifacts/ as a .trx, the standard form: the
    # whole of a failure's output is there when the console has scrolled
    # it away, and CI can attach it.
    Native dotnet test src/Shoddy.Tests --logger 'trx;LogFileName=Shoddy.Tests.trx' `
        --results-directory artifacts/test-results
    # The core suites: the golden library suite, the builtin surface check,
    # and the numerics/property suites. The roster is CORE_SUITES in
    # scripts/suites.mjs - THE ONLY COPY, shared with build.sh and proved
    # complete by scripts/verify-suites.js - and the reasoning for each
    # suite is written beside its name there.
    foreach ($suite in (Read-Roster 'CORE_SUITES')) {
        Write-Host "==> tst/$suite.shoddy"
        Native $Mill run "tst/$suite.shoddy"
    }
    # The net demo is the only end-to-end exercise of the socket words: it
    # stands up a server, connects a client to it and trades lines, both
    # ends in one process on loopback. --allow-net is required because the
    # network is a gated capability - without the flag the first socket
    # word aborts, so this run also proves the gate opens.
    Native $Mill run --allow-net tst/net-demo.shoddy
    # The tutorial's program is documentation that has to keep working:
    # its pure half is headless by design, so the checks run here beside
    # everything else and the tutorial cannot drift from code that no
    # longer compiles.
    Native $Mill run tutorials/spiro/test.shoddy
    # The sinq demo is the same argument for a machine rather than a
    # tutorial: docs/machines/sinq.html reproduces it line for line, so
    # running it here is what stops the page showing a program that no
    # longer compiles. It asserts nothing - tst/sinq.shoddy grades the
    # behaviour; this proves the example still READS as documented.
    Native $Mill run tst/sinq-demo.shoddy
    # Everything else the tree can prove about itself: a suite for every
    # machine that has one, and every mill's own suite. Nothing is
    # released that has not been through this.
    Invoke-MachineSuites
    Invoke-MillSuites
}

# The machine suites. Every machines/<name>.shoddy that has one is graded
# here, by name rather than by glob: a suite that stops being listed is a
# suite that stops running, and that is how nine of them once stopped
# running unnoticed. The list itself lives in scripts/suites.mjs -
# THE ONLY COPY. This script and build.sh both read it through node, after
# three hand-kept copies drifted apart and dropped seedterminaltest
# between them; scripts/verify-suites.js (build check, and CI) proves the
# roster complete. isamdump runs after isamtest and takes DELETE, because
# it reopens the file isamtest leaves behind - the round trip across two
# processes is the thing being proved, and DELETE clears up after it.
function Invoke-MachineSuites {
    $suites = Read-Roster 'MACHINE_SUITES'
    foreach ($suite in $suites) {
        Write-Host "==> tst/$suite.shoddy"
        Native $Mill run "tst/$suite.shoddy"
    }
    # seedscribblertest, seedturtletest and seedplottertest are NOT run
    # here. --no-window hides the window, but it is still a real one, and
    # GLFW has no platform to open it on on GitHub's hosted runners - no
    # X server's worth of libraries even under Xvfb (v1.10.1 shipped that
    # gate and it still failed the same way). Run them by hand, locally,
    # on a machine with a real display: scripts/shoddy-display.ps1.
    # isamtest leaves _isamtest.tmp and its index for isamdump to reopen,
    # and both are written where the program runs: a scratch folder, so
    # nothing a suite writes lands in the tree.
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('shoddy-isam-' + [System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $millPath = (Resolve-Path $Mill).Path
    Push-Location $scratch
    try {
        Write-Host '==> tst/isamtest.shoddy'
        Native $millPath run (Join-Path $PSScriptRoot 'tst/isamtest.shoddy')
        Write-Host '==> tst/isamdump.shoddy'
        Native $millPath run (Join-Path $PSScriptRoot 'tst/isamdump.shoddy') DELETE
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force $scratch
    }
}

# Every mill's own test target, through the mill's own build.ps1, so there
# is one statement of how a mill is tested and it lives with the mill.
# Every directory under mills/ is visited: a mill with no test target
# fails here with its usage message, which is the intended answer to
# shipping one without a suite.
#
# Every mill carries a native build.ps1 beside its build.sh, so this runs
# PowerShell to PowerShell and needs no bash on the machine at all. A mill
# missing its .ps1 fails loudly rather than silently falling back, because
# a silent fallback is how the gap lasted this long.
function Invoke-MillSuites {
    foreach ($m in Get-ChildItem mills -Directory | Sort-Object Name) {
        $script = Join-Path $m.FullName 'build.ps1'
        if (-not (Test-Path $script)) {
            [Console]::Error.WriteLine("mills/$($m.Name) has no build.ps1 - every mill carries both twins.")
            exit 1
        }
        Write-Host "==> mills/$($m.Name)/build.ps1 test"
        & $script test
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

# The fast gates - the doc and boundary verifiers, all Node and all quick.
# The same list CI's checks job runs; verify-permissions STAGES its fix
# locally (CI runs it with --check, which fails instead - a gate that
# repairs the evidence is not a gate).
function Invoke-Check {
    foreach ($gate in 'verify-docs', 'verify-errors', 'verify-permissions',
                      'verify-host-blind', 'verify-suites', 'verify-twins', 'verify-lanes') {
        Write-Host "==> scripts/$gate.js"
        Native node "scripts/$gate.js"
    }
    # The VS Code extension's own logic (the debug launch), tested with no
    # editor running.
    Write-Host '==> vscode-shoddy/test'
    Native node --test vscode-shoddy/test/launch.test.js
}

# An exact release number, as release.yml accepts it from a tag.
function Test-Version([string]$Text) {
    return $Text -cmatch '^[0-9]+\.[0-9]+\.[0-9]+$'
}

# Package the extension as $Version without changing package.json. vsce
# names and labels the package from package.json, so the version has to be
# in that file while vsce runs. The file is copied aside first and moved
# back on every way out: success, failure, Ctrl+C. A copy left behind
# means a run was killed outright, and package.json may still carry the
# stamp, so the next run stops and says how to put it back rather than
# guessing which of the two files is the real one.
# Runs in vscode-shoddy/.
function Invoke-PackageAs([string]$Version) {
    $kept = Join-Path '..' (Join-Path 'artifacts' 'package.json.unstamped')
    if (Test-Path $kept) {
        [Console]::Error.WriteLine("$kept is left from an interrupted vsix run.")
        [Console]::Error.WriteLine('Check it, then put it back: Move-Item -Force artifacts/package.json.unstamped vscode-shoddy/package.json')
        exit 1
    }
    New-Item -ItemType Directory -Path (Join-Path '..' 'artifacts') -Force | Out-Null
    Copy-Item package.json $kept
    try {
        Native node -e "const f=require('fs');const p=JSON.parse(f.readFileSync('package.json','utf8'));p.version=process.argv[1];f.writeFileSync('package.json',JSON.stringify(p,null,2)+'\n')" $Version
        Native vsce package
    }
    finally { Move-Item -Force $kept package.json }
}

# vsce is the ONE target with a dependency outside this repo's own tools,
# and it is unavoidable: a .vsix is a VS Code extension package and there is
# no dotnet or Shoddy path to producing one. It is REPORTED rather than
# installed: a build script must not put software on your machine,
# globally, without asking. release.yml installs vsce as its own explicit
# step before it calls this.
function Invoke-Vsix {
    param([string]$Bump)
    if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
        [Console]::Error.WriteLine('vsix needs @vscode/vsce, which is not installed.')
        [Console]::Error.WriteLine('  npm install -g @vscode/vsce      (needs Node.js)')
        [Console]::Error.WriteLine('Every other target - build, test, run, weave, stage,')
        [Console]::Error.WriteLine('clean - needs only PowerShell, dotnet and node.')
        exit 1
    }
    if ($Bump -and (Test-Version $Bump)) {
        Invoke-Stage $Bump
        Push-Location vscode-shoddy
        try { Invoke-PackageAs $Bump }
        finally { Pop-Location }
    }
    else {
        Invoke-Stage
        Push-Location vscode-shoddy
        try {
            if ($Bump) { Native vsce package $Bump --no-git-tag-version }
            else { Native vsce package }
        }
        finally { Pop-Location }
    }
}

# The other target with a dependency outside the repo's tools - the
# `code` CLI - and, like vsce, it is REPORTED rather than acquired. It
# installs the .vsix for the version given, or else the version
# package.json currently names, and refuses if that exact package has not
# been built: installing whatever .vsix happens to be newest is how a
# stale extension gets mistaken for a fresh one.
#
# Deliberately NOT part of `all`: release.yml runs `all` on a runner
# with no VS Code, and installing into an editor is a workstation act,
# not a build step. Locally the pair is: ./build.ps1 all, then install.
function Invoke-Install {
    param([string]$Version)
    if (-not (Get-Command code -ErrorAction SilentlyContinue)) {
        [Console]::Error.WriteLine('install needs the VS Code CLI (code), which is not on PATH.')
        exit 1
    }
    if ($Version) {
        if (-not (Test-Version $Version)) { [Console]::Error.WriteLine('usage: ./build.ps1 install [X.Y.Z]'); exit 2 }
        $ver = $Version
    }
    else {
        $ver = (Get-Content (Join-Path 'vscode-shoddy' 'package.json') -Raw | ConvertFrom-Json).version
    }
    $pkg = Join-Path 'vscode-shoddy' "vscode-shoddy-$ver.vsix"
    if (-not (Test-Path $pkg)) {
        [Console]::Error.WriteLine("no package for the current version: $pkg is not there.")
        [Console]::Error.WriteLine('run ./build.ps1 vsix (or all) first - install does not guess at a stale package.')
        exit 1
    }
    Native code --install-extension $pkg --force
    Write-Host "installed $pkg - reload VS Code windows to pick it up."
}

# Every MSBuild output lives under artifacts/, and the stage is the one
# other place a build writes. Anything else git ignores - a packaged
# .vsix, a log - is yours: `git clean -Xdf` removes it all, and asks
# nothing.
function Invoke-Clean {
    foreach ($d in 'artifacts', $StageMill, (Join-Path 'vscode-shoddy' 'machines')) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    Write-Host 'cleaned.'
}

switch ($Command) {
    'all' {
        # The build.sh twin re-invokes itself per step; here the steps are
        # called as functions instead, because a .ps1 invoked with & does
        # not reliably surface its exit code to the caller - the explicit
        # $LASTEXITCODE guards inside each function are what stop the chain,
        # so a red test never reaches the packager.
        Invoke-Clean
        Invoke-Test
        Invoke-Vsix $File
    }
    'build' { Invoke-Build }
    'machines' { Invoke-Build }
    'test' { Invoke-Test }
    'check' { Invoke-Check }
    'run' {
        # Switches before the file are the mill's own and pass through
        # as-is; everything after the file is the program's, reaching it
        # through Args (weather-glass takes its ZIP there). $File is only
        # the first word after `run`, which may be a switch, so the words
        # are rejoined and read in order exactly as build.sh reads them.
        # An unknown switch is refused here rather than handed on, where
        # the mill would take it for the file name.
        $usage = 'usage: ./build.ps1 run [--allow-net] [--no-window] [--no-lint] [--lint-verbose] FILE.shoddy [ARGS...]'
        $words = @()
        if ($File) { $words += $File }
        if ($Rest) { $words += $Rest }
        $switches = @()
        $i = 0
        while ($i -lt $words.Count -and $words[$i] -like '--*') {
            if ($words[$i] -notin '--allow-net', '--no-window', '--no-lint', '--lint-verbose') {
                [Console]::Error.WriteLine("unknown run switch: $($words[$i])")
                [Console]::Error.WriteLine($usage)
                exit 2
            }
            $switches += $words[$i]
            $i++
        }
        if ($i -ge $words.Count) { [Console]::Error.WriteLine($usage); exit 2 }
        Invoke-Build
        Native $Mill run @switches @($words[$i..($words.Count - 1)])
    }
    'weave' {
        if (-not $File) { [Console]::Error.WriteLine('usage: ./build.ps1 weave FILE.shoddy'); exit 2 }
        Invoke-Build
        Native $Mill weave $File
    }
    'stage' { Invoke-Stage $File }
    'vsix' { Invoke-Vsix $File }
    'install' { Invoke-Install $File }
    'clean' { Invoke-Clean }
    'help' {
        Get-Content $PSCommandPath | Select-Object -Skip 1 -First 51 | ForEach-Object { $_ -replace '^# ?', '' }
    }
    default {
        [Console]::Error.WriteLine("unknown command: $Command")
        [Console]::Error.WriteLine("run './build.ps1 help' for usage.")
        exit 2
    }
}
