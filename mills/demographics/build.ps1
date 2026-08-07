#!/usr/bin/env pwsh
# Build / run the demographics mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1            build both programs into bin/
#   ./build.ps1 build      same as above
#   ./build.ps1 train      build if needed, then train the model
#                          (writes dat/people-model.bin; ~9 minutes)
#   ./build.ps1 run        build if needed, then predict interactively
#   ./build.ps1 test       grade the shipped model against the data files
#   ./build.ps1 clean      remove built binaries from bin/
#
# The build weaves demographics-train.shoddy (the trainer) and
# demographics.shoddy (the predictor) to self-contained assemblies and
# drops every binary (the programs, runtimeconfigs, Shoddy.Runtime.dll)
# into bin/. To just run already-built programs, no rebuild:
#
#   dotnet bin/demographics-train.dll
#   dotnet bin/demographics.dll
#
# Neither takes arguments - the data and model paths are fixed at dat/,
# relative to this directory, and train/run always run from here
# regardless of where you invoke this script from: the model is the
# mill's own asset, not something to relocate by choice of cwd. Train
# first; the predictor aborts (politely) without dat/people-model.bin.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'build'
)

$ErrorActionPreference = 'Stop'

# Push rather than Set. Set-Location changes the SESSION's directory, not a
# scope's, so a mill script run from the repo root - which is how
# ../../build.ps1 runs all twelve - would leave the caller's shell parked in
# the mill's folder after it finished. The finally runs on `exit` too, so
# every path out of the switch below restores where you were.
Push-Location $PSScriptRoot
try {
    $MillDir = $PSScriptRoot
    . (Join-Path $MillDir '../../scripts/mill-common.ps1')

    $TrainOut = 'bin/demographics-train.dll'
    $RunOut = 'bin/demographics.dll'

    function Invoke-BuildMill {
        Invoke-Weave demographics-train.shoddy, demographics.shoddy
        Write-Host "built -> $TrainOut, $RunOut"
    }

    switch ($Command) {
        'build' {
            Invoke-BuildMill
        }
        'train' {
            if (-not (Test-Path $TrainOut)) { Invoke-BuildMill }
            & dotnet $TrainOut
            exit $LASTEXITCODE
        }
        'run' {
            if (-not (Test-Path $RunOut)) { Invoke-BuildMill }
            & dotnet $RunOut
            exit $LASTEXITCODE
        }
        'test' {
            # Run from source, not from bin/: the point is to grade what is
            # in the tree. No training - nine minutes, and it would rewrite
            # the model being graded. test.shoddy says so at more length.
            Assert-Mill
            & $Mill run test.shoddy
            exit $LASTEXITCODE
        }
        'clean' {
            $junk = @(Get-ChildItem bin/*.dll, bin/*.json -ErrorAction SilentlyContinue)
            if ($junk.Count -gt 0) { Remove-Item -Force $junk }
            Write-Host 'cleaned.'
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [build|train|run|test|clean]')
            exit 2
        }
    }

    exit 0
}
finally { Pop-Location }
