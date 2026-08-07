#!/usr/bin/env pwsh
# Run The Oregon Trail (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1          run the game
#   ./build.ps1 run      same as above
#   ./build.ps1 test     run the headless model checks
#
# Oregon is a console program: prompts in, text out, no window. The
# pure model in oregon-core.shoddy is what test.shoddy covers; this
# wrapper just launches the interactive game. When told to TYPE a
# word (BANG, BLAM, POW, WHAM), type it fast and press Enter.
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
            & $Mill run oregon.shoddy
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
