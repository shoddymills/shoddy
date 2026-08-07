#!/usr/bin/env pwsh
# Run Devil's Dust (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1          run the demo
#   ./build.ps1 run      same as above
#   ./build.ps1 test     run the headless model checks
#
# Devil's Dust is a scribbler program: it opens a window, so it runs
# under `mill run` (a woven `dotnet FILE.dll` has no window backend).
# The pure model in devils-dust-core.shoddy is what the tests cover;
# this wrapper just launches the windowed demo. Q or Escape quits.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run'
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

    switch ($Command) {
        'run' {
            Assert-Mill
            & $Mill run devils-dust.shoddy
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            & $Mill run test.shoddy
            exit $LASTEXITCODE
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|test]')
            exit 2
        }
    }

    exit 0
}
finally { Pop-Location }
