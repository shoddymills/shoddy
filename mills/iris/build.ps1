#!/usr/bin/env pwsh
# Build / run the iris mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1            build both programs into bin/
#   ./build.ps1 build      same as above
#   ./build.ps1 train      build if needed, then train the model
#                          (writes dat/iris-model.bin; a few seconds)
#   ./build.ps1 run        build if needed, then classify interactively
#   ./build.ps1 test       grade the shipped model against the held-out rows
#   ./build.ps1 clean      remove built binaries from bin/
#
# The build weaves iris-train.shoddy (the trainer) and iris.shoddy (the
# predictor) to self-contained assemblies and drops every binary (the
# programs, runtimeconfigs, Shoddy.Runtime.dll) into bin/. To just run
# already-built programs, no rebuild:
#
#   dotnet bin/iris-train.dll
#   dotnet bin/iris.dll
#
# Neither takes arguments - the data and model paths are fixed at dat/,
# relative to this directory, and train/run always run from here
# regardless of where you invoke this script from: the model is the
# mill's own asset, not something to relocate by choice of cwd. Train
# first; the predictor aborts (politely) without dat/iris-model.bin.
#
# This is the classification counterpart to the demographics mill.
# Demographics predicts a number and is scored on how close it gets;
# this predicts a category, is scored on how often it is right, and
# reports how sure it was. Training takes seconds rather than nine
# minutes, which makes it the one to reach for when changing
# machines/neural.shoddy and wanting to know quickly whether it still
# works.
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

    $TrainOut = 'bin/iris-train.dll'
    $RunOut = 'bin/iris.dll'

    function Invoke-BuildMill {
        Invoke-Weave iris-train.shoddy, iris.shoddy
        Write-Host "built -> $TrainOut, $RunOut"
    }

    switch ($Command) {
        'build' {
            Invoke-BuildMill
        }
        'train' {
            if (-not (Test-Path $TrainOut)) { Invoke-BuildMill }
            Invoke-Native { & dotnet $TrainOut }
            exit $LASTEXITCODE
        }
        'run' {
            if (-not (Test-Path $RunOut)) { Invoke-BuildMill }
            Invoke-Native { & dotnet $RunOut }
            exit $LASTEXITCODE
        }
        'test' {
            # Run from source, not from bin/: the point is to grade what is
            # in the tree. No training - test.shoddy explains why.
            Assert-Mill
            Invoke-Native { & $Mill run test.shoddy }
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
