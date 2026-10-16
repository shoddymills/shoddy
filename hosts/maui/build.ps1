#!/usr/bin/env pwsh
# Build the MAUI lane: restore, build and test whatever this machine's
# workloads can build (today: Windows). The lane's projects build the
# toolchain they weave with and reference the machine library, so a
# plain dotnet build is the whole build.
#
#   ./build.ps1          restore + build (Debug)
#   ./build.ps1 test     build + run Shoddy.Maui.Tests
#   ./build.ps1 run      build + launch the Reckoner app
#   ./build.ps1 release  Release build (release artifacts carry no perch - B7.4)
[CmdletBinding()]
param([Parameter(Position = 0)][string]$Command = 'build')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# A node gate, judged on its exit code alone, as Run judges dotnet.
function Gate([string[]]$NodeArgs) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & node @NodeArgs } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: node $($NodeArgs -join ' ') failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

function Run([string[]]$DotnetArgs) {
    Write-Host ("> dotnet " + ($DotnetArgs -join ' ')) -ForegroundColor Cyan
    # dotnet writes restore progress to stderr under some hosts; judge
    # the call on its exit code alone - the only honest signal a native
    # program gives.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { dotnet @DotnetArgs } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: dotnet $($DotnetArgs -join ' ') failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

switch ($Command) {
    'build'   { Run @('build', 'ShoddyMaui.slnx') }
    'test'    { Run @('test', 'Shoddy.Maui.Tests') }
    'run'     { Run @('build', 'shoddy-reckoner'); Run @('run', '--project', 'shoddy-reckoner', '--no-build') }
    # release grades its own closure (B7.4): no perch, the woven core and
    # the machine library present (scripts/verify-closure.js).
    'release' { Run @('build', 'ShoddyMaui.slnx', '-c', 'Release'); Gate @('../../scripts/verify-closure.js', 'reckoner') }
    default   { Write-Host "unknown command: $Command (build | test | run | release)" -ForegroundColor Red; exit 1 }
}
