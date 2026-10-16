#!/usr/bin/env pwsh
# Run Thornle (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1          run the game
#   ./build.ps1 run      same as above
#   ./build.ps1 short    run the game on files/short-list.txt, six words
#   ./build.ps1 test     run the headless checks, weave the shell so its
#                        weave and lint are checked too, then play one
#                        scripted session through the real shell: a short
#                        line, a word not in the list, a word that is,
#                        leave; colour off, one more word, leave, quit.
#                        A hang or a crash there is a failure; the frames
#                        it paints are not checked.
#
# Thornle is a VT100 terminal program - no scribbler, no window: it draws
# with escape sequences and reads each guess as a whole line through the
# InputLine builtin, so it wants an ANSI-capable terminal at least 80x24.
# Windows Terminal or a modern PowerShell console will do; the legacy
# conhost window will not render the 24-bit colours. The pure model in
# thornle-core.shoddy is what the checks cover; this wrapper just
# launches the game.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run'
)

$ErrorActionPreference = 'Stop'

# Push rather than Set, so every path out restores where you were.
Push-Location $PSScriptRoot
try {
    $MillDir = $PSScriptRoot
    . (Join-Path $MillDir '../../scripts/shoddy-mill-common.ps1')

    switch ($Command) {
        'run' {
            Assert-Mill
            Invoke-Native { & $Mill run thornle.shoddy }
            exit $LASTEXITCODE
        }
        'short' {
            Assert-Mill
            Invoke-Native { & $Mill run thornle.shoddy files/short-list.txt }
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            Invoke-Native { & $Mill run test.shoddy }
            Invoke-Native { & $Mill weave thornle.shoddy }
            Invoke-Native { @('P', 'cran', 'zzzzz', 'speed', 'q', 'C', 'P', 'speed', 'q', 'q') | & $Mill run thornle.shoddy files/words.txt 42 }
            exit $LASTEXITCODE
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|short|test]')
            exit 2
        }
    }

    exit 0
}
finally { Pop-Location }
