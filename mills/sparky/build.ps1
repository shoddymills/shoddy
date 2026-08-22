#!/usr/bin/env pwsh
# Build / run the sparky mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1 run              the dictionary, at a prompt
#   ./build.ps1 test             the headless suite: no terminal needed
#   ./build.ps1 build            weave the program into bin/
#
# Sparky's product is an MCP server (hosts/mcp) and its caller is a
# model. What this script builds is the mill underneath it: the same
# fold, the same dictionary, reachable by a person at a terminal. `run`
# is here so the fold can be looked at, and `test` is the gate that says
# the fold is still what it was.
#
# run is LIVE: it launches from wherever you actually typed .\build.ps1,
# so sparkyrc on the way in resolves against your own shell, exactly as
# it would running the mill directly. test stays pinned to this
# directory: it reads the mill's own suite, not anything you typed.
#
# The test target needs NOTHING - no terminal, no display, no network,
# and it writes no files at all. Everything between a typed line and the
# lines it produces is pure, which is the whole reason
# sparky-core.shoddy and sparky.shoddy are separate files.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run'
)

$ErrorActionPreference = 'Stop'
$OrigLocation = Get-Location

# Push rather than Set, so a mill script run from the repo root - which
# is how ../../build.ps1 runs all of them - does not leave the caller's
# shell parked in this folder afterwards. The finally runs on `exit` too.
Push-Location $PSScriptRoot
try {
    $MillDir = $PSScriptRoot
    . (Join-Path $MillDir '../../scripts/shoddy-mill-common.ps1')

    switch ($Command) {
        'run' {
            Assert-Mill
            Invoke-Live $OrigLocation { & $Mill run (Join-Path $MillDir 'sparky.shoddy') }
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            Invoke-Native { $null | & $Mill run test.shoddy }
            exit $LASTEXITCODE
        }
        'build' {
            Invoke-Weave sparky.shoddy
            Write-Host 'woven into bin/'
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|test|build]')
            exit 2
        }
    }

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like build could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
