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
# relative to this directory, so run from here. Train first; the
# predictor aborts (politely) without dat/iris-model.bin.
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

    $Repo = '../..'
    $Mill = Join-Path $Repo 'bin/mill.exe'
    $TrainOut = 'bin/iris-train.dll'
    $RunOut = 'bin/iris.dll'

    function Assert-Mill {
        if (-not (Test-Path $Mill)) {
            Write-Host "mill toolchain not built; building it into $Repo/bin ..."
            dotnet publish (Join-Path $Repo 'src/Shoddy.Mill') -c Release -o (Join-Path $Repo 'bin')
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    # The weave drops its output beside the source; this moves it into bin/,
    # the same shuffle the .sh does with mv -f. Shoddy.*.dll may or may not be
    # there depending on what the weave decided to copy, so its absence is not
    # an error.
    function Invoke-BuildMill {
        Assert-Mill
        & $Mill weave iris-train.shoddy
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        & $Mill weave iris.shoddy
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        New-Item -ItemType Directory -Path bin -Force | Out-Null
        Move-Item -Force iris-train.dll, iris-train.runtimeconfig.json bin/
        Move-Item -Force iris.dll, iris.runtimeconfig.json bin/
        $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
        if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
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
            # in the tree. No training - test.shoddy explains why.
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

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
