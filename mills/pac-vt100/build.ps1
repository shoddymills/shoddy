#!/usr/bin/env pwsh
# Run Shoddy Pac (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1          run the game
#   ./build.ps1 test     run the headless simulation smoke check
#
# Pac is a VT100 terminal program - no scribbler, no window: it draws
# with escape sequences and reads keys through the InKey builtin, so it
# wants a real ANSI-capable terminal at least 80x26. Windows Terminal or
# a modern PowerShell console will do; the legacy conhost window will
# not. The pure model in pac-core.shoddy is what the unit tests cover;
# this wrapper just launches the game. Controls: WASD / arrows / keypad
# move, Q or Escape quits.
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

    $Repo = '../..'
    $Mill = Join-Path $Repo 'bin/mill.exe'

    function Assert-Mill {
        if (-not (Test-Path $Mill)) {
            Write-Host "mill toolchain not built; building it into $Repo/bin ..."
            dotnet publish (Join-Path $Repo 'src/Shoddy.Mill') -c Release -o (Join-Path $Repo 'bin')
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    switch ($Command) {
        'run' {
            Assert-Mill
            & $Mill run pac.shoddy
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

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
