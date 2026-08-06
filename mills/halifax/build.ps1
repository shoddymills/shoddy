#!/usr/bin/env pwsh
# Build / run the halifax mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1 run              the calculator, at a prompt
#   ./build.ps1 test             the headless suite: no terminal needed
#   ./build.ps1 demo             the golden session, fed from files/demo.halifax
#   ./build.ps1 build            weave the program into bin/
#
# run is LIVE: it launches from wherever you actually typed .\build.ps1,
# not from this directory - so a path you give SAVE, LOAD or TAPESAVE at
# the prompt, and halifaxrc on the way in, resolve against your own
# shell, exactly as they would running the calculator directly. test and
# demo stay pinned to this directory: they read the mill's own fixtures
# (test.shoddy, files/demo.halifax), not anything you typed.
#
# demo feeds files/demo.halifax to the prompt: the R5.2 sequence, in
# order, ending on a traced cascade. It is there to show the shell works
# end to end, not to look like a session - redirected input is not echoed
# back, so each answer starts on the prompt line that asked for it. In a
# terminal your own keystrokes fill that gap, which is what the docs
# page's transcript shows and what the demo GIF is recorded from.
#
# The test target needs NOTHING - no terminal, no display, no network.
# Everything between a typed line and the lines it produces is pure,
# which is the whole reason halifax-core.shoddy and halifax.shoddy are
# separate files.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run'
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
    . (Join-Path $MillDir '../../scripts/mill-common.ps1')

    switch ($Command) {
        'run' {
            Assert-Mill
            Invoke-Live $OrigLocation { & $Mill run (Join-Path $MillDir 'halifax.shoddy') }
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            $null | & $Mill run test.shoddy
            exit $LASTEXITCODE
        }
        'demo' {
            Assert-Mill
            Get-Content files/demo.halifax | & $Mill run halifax.shoddy
            exit $LASTEXITCODE
        }
        'build' {
            Invoke-Weave halifax.shoddy
            Write-Host 'woven into bin/'
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|test|demo|build]')
            exit 2
        }
    }

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like build could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
