#!/usr/bin/env pwsh
# Build the MAUI lane: restore, build and test whatever this machine's
# workloads can build (today: Windows). Every call first runs the repo
# root's build, which publishes bin/mill.exe - ShoddyWeave drives it.
#
#   ./build.ps1          restore + build (Debug)
#   ./build.ps1 test     build + run Shoddy.Maui.Tests
#   ./build.ps1 run      build + launch the Reckoner app
#   ./build.ps1 release  Release build (release artifacts carry no perch - B7.4)
[CmdletBinding()]
param([Parameter(Position = 0)][string]$Command = 'build')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# The root's build, every call: a check that bin/mill.exe exists passes a
# mill from before a compiler change. Invoke-Build in ../../build.ps1
# says why. Push/Pop, because the root script sets its own location.
Push-Location (Join-Path $PSScriptRoot '../..')
try { & ./build.ps1 build } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    Write-Host "STOPPED: the repo root's build failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
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
    'release' { Run @('build', 'ShoddyMaui.slnx', '-c', 'Release') }
    default   { Write-Host "unknown command: $Command (build | test | run | release)" -ForegroundColor Red; exit 1 }
}
