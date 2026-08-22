#!/usr/bin/env pwsh
# Build / run the simplex-from-mps mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1            build the program into bin/
#   ./build.ps1 build      same as above
#   ./build.ps1 run FILE   build if needed, then run on an MPS FILE
#   ./build.ps1 test       solve both fixtures and check the answers
#   ./build.ps1 clean      remove built binaries from bin/
#
# run is LIVE: FILE (and -z) are resolved from wherever you actually
# typed .\build.ps1, not from this directory - so a path you give at the
# command line works the way it would for any ordinary program.
#
# The build weaves simplex-mps.shoddy to a self-contained assembly and
# drops every binary (the program, its runtimeconfig, Shoddy.Runtime.dll)
# into bin/. To just run an already-built program, no rebuild:
#
#   dotnet bin/simplex-mps.dll files/blend.mps        # or files/mix.mps
#
# Add -z (or --zero-lower) to force x >= 0 on every variable.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'build',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$OrigLocation = Get-Location

# Push rather than Set. Set-Location changes the SESSION's directory, not a
# scope's, so a mill script run from the repo root - which is how
# ../../build.ps1 runs all twelve - would leave the caller's shell parked in
# the mill's folder after it finished. The finally runs on `exit` too, so
# every path out of the switch below restores where you were.
Push-Location $PSScriptRoot
try {
    $MillDir = $PSScriptRoot
    . (Join-Path $MillDir '../../scripts/shoddy-mill-common.ps1')

    $Src = 'simplex-mps.shoddy'
    $Out = 'bin/simplex-mps.dll'

    function Invoke-BuildMill {
        Invoke-Weave $Src
        Write-Host "built -> $Out"
    }

    switch ($Command) {
        'build' {
            Invoke-BuildMill
        }
        'run' {
            if (-not $Rest -or $Rest.Count -lt 1) {
                [Console]::Error.WriteLine('usage: ./build.ps1 run FILE.mps [-z]')
                exit 2
            }
            if (-not (Test-Path $Out)) { Invoke-BuildMill }
            Invoke-Live $OrigLocation { & dotnet (Join-Path $MillDir $Out) @Rest }
            exit $LASTEXITCODE
        }
        'test' {
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
            [Console]::Error.WriteLine('usage: ./build.ps1 [build|run FILE.mps|test|clean]')
            exit 2
        }
    }

    exit 0
}
finally { Pop-Location }
