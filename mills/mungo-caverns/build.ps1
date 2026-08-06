#!/usr/bin/env pwsh
# Run Mungo Caverns (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1          play the game
#   ./build.ps1 run      same as above
#   ./build.ps1 test     run every headless suite (test.shoddy, tests/test-*)
#
# mungo-caverns is a console program: prompts in, text out, no window. Words
# are significant to five letters, as they have been since 1977 -- XYZZY,
# PLUGH, and PLOVER all still work.
#
# The cave lives in the mungo-caverns-*.shoddy tables, which are
# hand-edited source.
#
# Testing is five headless suites.  test.shoddy covers the generator, the
# tables and the parser; tests/test-tables.shoddy asserts what must be
# true of the cave itself; tests/test-turn.shoddy drives whole turns as a
# function; tests/test-walk.shoddy plays a game to the gold and back; and
# tests/test-fuzz.shoddy throws random commands at it.  194 assertions,
# about twenty seconds, and "test" runs the lot.  See tests/README.
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
            & $Mill run mungo-caverns.shoddy
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            # test.shoddy first, then the tests/ suites in name order, which is
            # the order the .sh glob produces. $null on the pipeline is this
            # shell's `< /dev/null`: every one of these reads commands, and a
            # suite left waiting on a keystroke would hang the run rather than
            # fail it.
            $suites = @('test.shoddy') +
                      @(Get-ChildItem tests/test-*.shoddy | Sort-Object Name |
                            ForEach-Object { 'tests/' + $_.Name })
            foreach ($suite in $suites) {
                Write-Host "--- $suite"
                $null | & $Mill run $suite
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            }
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
