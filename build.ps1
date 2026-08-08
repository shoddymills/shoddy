#!/usr/bin/env pwsh
# Shoddy build wrapper (Windows). Unix users: use build.sh (same commands).
#
#   ./build.ps1 all [bump]            everything: clean, test, then .vsix
#   ./build.ps1 build                 build the mill into bin/
#   ./build.ps1 test                  everything: dotnet tests, the machine
#                                     suites and every mill's own suite
#   ./build.ps1 run FILE.shoddy       compile in memory and run a program
#   ./build.ps1 weave FILE.shoddy     compile a program to an assembly
#   ./build.ps1 machines              compile every machine to a machine DLL
#   ./build.ps1 stage                 stage the mill + machines into the extension
#   ./build.ps1 vsix [bump]           package the VS Code extension (.vsix)
#   ./build.ps1 clean                 remove build output
#   ./build.ps1 help                  show this help
#
# all: the whole toolchain from nothing — clean, then test (which rebuilds
# the mill), then vsix (which builds the machines, stages, and packages).
# This is the path for a package you mean to install or ship; `vsix` on its
# own reuses whatever bin/mill is already there and runs no tests, which is
# fine for a quick turn and not for a release. Takes the same optional bump
# as vsix: ./build.ps1 all patch
#
# vsix [bump]: optional patch|minor|major or an exact X.Y.Z to bump the
# extension version before packaging (e.g. ./build.ps1 vsix patch).
# vsix stages first, so the package carries its own mill and machines.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$File
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Mill = Join-Path 'bin' 'mill.exe'
# Where `stage` drops the extension's own copy of the toolchain. Both are
# build output: gitignored, and rebuilt from scratch on every stage.
$StageMill = Join-Path 'vscode-shoddy' 'mill'
$StageLib = Join-Path 'vscode-shoddy' 'machines'

function Invoke-Build {
    dotnet publish src/Shoddy.Mill -c Release -o bin
}

function Assert-Mill {
    if (-not (Test-Path $Mill)) {
        Write-Host 'mill not built; running build first...'
        Invoke-Build
    }
}

function Invoke-Machines {
    Assert-Mill
    # Dependency order: an Include "x.shoddy" resolves to the machine DLL
    # only if Shoddy.Machines.X.dll is already built — otherwise the
    # source is spliced in and its defs re-exported, which collides with
    # the real machine downstream (duplicate definition of ANY, etc.).
    $files = @(Get-ChildItem machines/*.shoddy, machines/seeds/*.shoddy | Sort-Object Name)
    $deps = @{}
    foreach ($f in $files) {
        $deps[$f.BaseName] = @(
            Select-String -Path $f.FullName -Pattern '^\s*Include\s+"(.+)\.shoddy"' |
                ForEach-Object { $_.Matches[0].Groups[1].Value.ToLowerInvariant() })
    }
    $built = New-Object 'System.Collections.Generic.HashSet[string]'
    $pending = [System.Collections.ArrayList]$files
    while ($pending.Count -gt 0) {
        $ready = @($pending | Where-Object {
            $unmet = @($deps[$_.BaseName] |
                Where-Object { $deps.ContainsKey($_) -and -not $built.Contains($_) })
            $unmet.Count -eq 0
        })
        if ($ready.Count -eq 0) {
            Write-Error ('include cycle among machines: ' +
                (($pending | ForEach-Object BaseName) -join ', '))
            exit 1
        }
        foreach ($f in $ready) {
            Write-Host "==> $($f.Name)"
            & $Mill machine $f.FullName
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            [void]$built.Add($f.BaseName.ToLowerInvariant())
            $pending.Remove($f)
        }
    }
}

# Give the extension its own toolchain, so installing the .vsix is the
# whole install: a framework-dependent mill (every RID's natives ride
# along in runtimes/, so one package runs everywhere the .NET runtime
# is) and the machines it needs. The extension points SHODDYLIB at the
# staged machines/, where Include finds the source and the resolver
# finds the DLL beside it in bin/.
function Invoke-Stage {
    Invoke-Machines
    foreach ($d in $StageMill, $StageLib) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    # Satellite resource assemblies are Roslyn's localized diagnostics —
    # ~5 MB of the package for messages the mill never surfaces.
    dotnet publish src/Shoddy.Mill -c Release -o $StageMill `
        -p:SatelliteResourceLanguages=en -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    New-Item -ItemType Directory -Path (Join-Path $StageLib 'bin') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $StageLib 'seeds/bin') -Force | Out-Null
    Copy-Item machines/*.shoddy $StageLib
    Copy-Item machines/bin/*.dll (Join-Path $StageLib 'bin')
    Copy-Item machines/seeds/*.shoddy (Join-Path $StageLib 'seeds')
    Copy-Item machines/seeds/bin/*.dll (Join-Path $StageLib 'seeds/bin')
    # One timestamp across every staged DLL, newer than every staged
    # source, and identical between them. Copy stamps each file as it
    # goes - alphabetically - and a machine counts as stale when a DLL
    # it depends on is newer than its own, so csv (depending on dict,
    # seq and str, all later in the alphabet) arrived looking stale and
    # rebuilt itself on first use, inside the installed extension.
    $stamp = Get-Date
    Get-ChildItem (Join-Path $StageLib 'bin'), (Join-Path $StageLib 'seeds/bin') -Filter *.dll |
        ForEach-Object { $_.LastWriteTime = $stamp }
    # Roslyn, Silk.NET, GLFW and OpenAL Soft are redistributed in the
    # package, so their notices have to travel with it — LGPL-2.1 for
    # OpenAL Soft, attribution for the rest. The authorship statement and
    # its AI disclosure travel with it for the same reason: the .vsix is
    # the whole install for most people, and never sees the repository.
    Copy-Item THIRD-PARTY-NOTICES.md vscode-shoddy
    Copy-Item AUTHORSHIP.md vscode-shoddy
    $size = (Get-ChildItem $StageMill, $StageLib -Recurse -File |
        Measure-Object Length -Sum).Sum
    Write-Host ('staged mill + machines into vscode-shoddy/ ({0:N1} MB)' -f ($size / 1MB))
}

function Invoke-Test {
    dotnet test src/Shoddy.Tests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Assert-Mill
    & $Mill run tst/libtest.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # Not a machine suite: it compares the RUNTIME's builtin dispatch against
    # the seeded dictionary, and fails if a builtin is reachable that a stated
    # rule says must not be, or if a future seed quietly claims a name the
    # builtin seed's work list expects to be free. Runs here, before the
    # machine suites, because a collision it catches is one they would report
    # somewhere else.
    & $Mill run tst/builtinsurfacetest.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # fin's arithmetic is the kind that produces a plausible wrong answer
    # rather than an error, so its known-answer suite runs in CI beside the
    # golden files rather than by hand.
    & $Mill run tst/fin.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # eng and lin are the same case: a numerics library that is subtly wrong
    # still returns numbers. eng's suite is known answers, lin's is those plus
    # residuals against the defining identities — P A - L U, A v - lambda v —
    # which is the only way to test a factorisation whose parts are not unique.
    & $Mill run tst/eng.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $Mill run tst/lin.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # alg is the same case again and then some: a symbolic answer that is
    # subtly wrong is still a well-formed expression. Its suite tests by
    # property rather than by value -- integrals differentiated back,
    # factorisations expanded, partial fractions recombined -- and it is
    # also the only place the alg/eng bridge can be exercised, since
    # neither machine includes the other.
    & $Mill run tst/alg.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # bool is the same case once more: it is arithmetic standing in for bits,
    # so a wrong fold or a formula that overflows 2^53 before its Mod runs
    # returns a plausible number rather than failing. Its suite is known
    # answers worked by hand plus the identities -- De Morgan both ways, Gray
    # codes one bit apart, a minimised cover rebuilt and compared -- which is
    # the only way to test a minimal form whose spelling is not unique.
    & $Mill run tst/bool.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # The net demo is the only end-to-end exercise of the socket words: it
    # stands up a server, connects a client to it and trades lines, both
    # ends in one process on loopback. --allow-net is required because the
    # network is a gated capability — without the flag the first socket
    # word aborts, so this run also proves the gate opens.
    & $Mill run --allow-net tst/net-demo.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # The tutorial's program is documentation that has to keep working:
    # its pure half is headless by design, so the checks run here beside
    # everything else and the tutorial cannot drift from code that no
    # longer compiles.
    & $Mill run tutorials/spiro/test.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # Everything else the tree can prove about itself: a suite for every
    # machine that has one, and every mill's own suite. Both existed
    # before and neither ran here, which meant a release could ship a
    # broken machine or a broken mill through a green gate. Nothing is
    # released that has not been through this.
    Invoke-MachineSuites
    Invoke-MillSuites
}

# The machine suites. Every machines/<name>.shoddy that has one is graded
# here, by name rather than by glob: a suite that stops being listed is a
# suite that stops running, and the whole point of this list is that nine
# of them had stopped running without anyone noticing. isamdump runs after
# isamtest and takes DELETE, because it reopens the file isamtest leaves
# behind - the round trip across two processes is the thing being proved,
# and DELETE clears up after it.
function Invoke-MachineSuites {
    Assert-Mill
    foreach ($suite in 'csvtest', 'cuttletest', 'htmltest', 'jsontest', 'nettest', 'neuraltest',
                       'randomtest', 'reckonertest', 'seedtest', 'seedbuiltintest', 'seedengtest',
                       'seedbuzzertest', 'seedfintest', 'seedhttpstest', 'seedisamtest', 'seedneuraltest', 'seednettest', 'seedreciotest', 'seedsimplextest', 'seedvt100test', 'shakertest',
                       'xmltest') {
        Write-Host "==> tst/$suite.shoddy"
        & $Mill run "tst/$suite.shoddy"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    # seedscribblertest, seedturtletest and seedplottertest are NOT run
    # here. --no-window hides the window, but it is still a real one, and
    # GLFW has no platform to open it on on GitHub's hosted runners — no
    # X server's worth of libraries even under Xvfb (v1.10.1 shipped that
    # gate and it still failed the same way). Run them by hand, locally,
    # on a machine with a real display: `bin/mill --no-window run
    # tst/seedscribblertest.shoddy` and the same for turtle/plotter.
    Write-Host '==> tst/isamtest.shoddy'
    & $Mill run tst/isamtest.shoddy
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host '==> tst/isamdump.shoddy'
    & $Mill run tst/isamdump.shoddy DELETE
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Every mill's own test target, through the mill's own build.ps1, so there
# is one statement of how a mill is tested and it lives with the mill.
# Every directory under mills/ is visited: a mill with no test target
# fails here with its usage message, which is the intended answer to
# shipping one without a suite.
#
# Every mill carries a native build.ps1 beside its build.sh, so this runs
# PowerShell to PowerShell and needs no bash on the machine at all. It
# used to hunt down Git Bash and run the .sh through it, which worked but
# meant a Windows contributor could not test a mill without installing a
# second shell — and meant the twins could drift, since only one of them
# was ever exercised here. A mill missing its .ps1 fails loudly rather
# than silently falling back, because a silent fallback is how the gap
# lasted this long.
function Invoke-MillSuites {
    Assert-Mill
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

# vsce is the ONE target with a dependency outside this repo's own tools,
# and it is unavoidable: a .vsix is a VS Code extension package and there is
# no dotnet or Shoddy path to producing one. Every other target here — and
# every mill's own wrapper — needs nothing but PowerShell, dotnet and
# bin/mill.
#
# It is REPORTED rather than installed. This used to run
# `npm install -g @vscode/vsce` when vsce was missing, which is a build
# script putting software on your machine, globally, without asking. CI does
# not need that behaviour either: release.yml installs vsce as its own
# explicit step before it calls this.
function Invoke-Vsix {
    param([string]$Bump)
    if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
        [Console]::Error.WriteLine('vsix needs @vscode/vsce, which is not installed.')
        [Console]::Error.WriteLine('  npm install -g @vscode/vsce      (needs Node.js)')
        [Console]::Error.WriteLine('Every other target - build, test, run, weave, machines,')
        [Console]::Error.WriteLine('stage, clean - needs only PowerShell and dotnet.')
        exit 1
    }
    Invoke-Stage
    Push-Location vscode-shoddy
    try {
        if ($Bump) { vsce package $Bump --no-git-tag-version }
        else { vsce package }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally { Pop-Location }
}

function Invoke-Clean {
    if (Test-Path bin) { Remove-Item -Recurse -Force bin }
    if (Test-Path artifacts) { Remove-Item -Recurse -Force artifacts }
    if (Test-Path machines/bin) { Remove-Item -Recurse -Force machines/bin }
    if (Test-Path machines/seeds/bin) { Remove-Item -Recurse -Force machines/seeds/bin }
    foreach ($d in $StageMill, $StageLib) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    Get-ChildItem src -Recurse -Directory -Include bin, obj |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
    Write-Host 'cleaned.'
}

switch ($Command) {
    'all' {
        # The build.sh twin re-invokes itself per step; here the steps are
        # called as functions instead, because a .ps1 invoked with & does
        # not reliably surface its exit code to the caller — the explicit
        # $LASTEXITCODE guards inside each function are what stop the chain,
        # so a red test never reaches the packager.
        Invoke-Clean
        Invoke-Test
        Invoke-Vsix $File
    }
    'build' { Invoke-Build }
    'test' { Invoke-Test }
    'run' {
        if (-not $File) { [Console]::Error.WriteLine('usage: ./build.ps1 run FILE.shoddy'); exit 2 }
        Assert-Mill
        & $Mill run $File
    }
    'weave' {
        if (-not $File) { [Console]::Error.WriteLine('usage: ./build.ps1 weave FILE.shoddy'); exit 2 }
        Assert-Mill
        & $Mill weave $File
    }
    'machines' { Invoke-Machines }
    'stage' { Invoke-Stage }
    'vsix' { Invoke-Vsix $File }
    'clean' { Invoke-Clean }
    { $_ -in 'help', '-h', '--help' } {
        Get-Content $PSCommandPath | Select-Object -Skip 1 -First 24 |
            ForEach-Object { $_ -replace '^#\s?', '' }
    }
    default {
        [Console]::Error.WriteLine("unknown command: $Command`nrun './build.ps1 help' for usage.")
        exit 2
    }
}
