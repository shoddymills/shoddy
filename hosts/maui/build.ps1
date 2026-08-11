#!/usr/bin/env pwsh
# Build the MAUI lane: restore, build and test whatever this machine's
# workloads can build (today: Windows). Run the repo root's build first
# so bin/mill.exe exists — ShoddyWeave drives it.
#
#   ./build.ps1          restore + build (Debug)
#   ./build.ps1 test     build + run Shoddy.Maui.Tests
#   ./build.ps1 run      build + launch the Reckoner app
#   ./build.ps1 release  Release build (release artifacts carry no perch - B7.4)
[CmdletBinding()]
param([Parameter(Position = 0)][string]$Command = 'build')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$mill = Join-Path $PSScriptRoot '..\..\bin\mill.exe'
if (-not (Test-Path $mill)) {
    Write-Host 'STOPPED: bin/mill.exe is missing - run ./build.ps1 at the repo root first.' -ForegroundColor Red
    exit 1
}

function Run([string[]]$DotnetArgs) {
    Write-Host ("> dotnet " + ($DotnetArgs -join ' ')) -ForegroundColor Cyan
    # dotnet writes restore progress to stderr under some hosts; judge
    # the call on its exit code alone (see shoddy-release.ps1's note).
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
