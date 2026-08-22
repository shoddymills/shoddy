#!/usr/bin/env pwsh
# Run the spirograph (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1          draw the spiral in a window
#   ./build.ps1 run      same as above
#   ./build.ps1 test     run the headless checks (no window; works in CI)
#
# The drawing opens a window, so it runs under `mill run` - a woven
# `dotnet spiro.dll` has no window backend. The pure half in
# spiro-core.shoddy is what test.shoddy covers, and that needs no display.
#
# Finding a mill, in order: $env:MILL if you set it, the repo's own bin/mill
# when this folder sits inside a checkout, then whatever `mill` is on the
# PATH - the VS Code extension carries one. Copy this file beside your own
# program and it keeps working wherever the folder lives. That is why this
# one does NOT source scripts/shoddy-mill-common.ps1 the way a mill's build.ps1
# does: mill-common assumes the repository layout, and a tutorial meant to
# be copied out of the tree cannot.
#
# This twin was missing. build.ps1 at the root refuses a mill that has only
# one of the pair, and nothing made the same check for tutorials, so spiro
# shipped a .sh alone - on a Windows machine, where the .ps1 is the half
# that actually gets run.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run'
)
$ErrorActionPreference = 'Stop'

# Push rather than Set. Set-Location moves the SESSION's directory, so a
# script run from somewhere else would leave the caller's shell parked here
# after it finished. The finally runs on `exit` too.
Push-Location $PSScriptRoot
try {
    function Find-Mill {
        if ($env:MILL) { return $env:MILL }
        foreach ($candidate in @('../../bin/mill.exe', '../../bin/mill')) {
            if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
        }
        if (Get-Command mill -ErrorAction SilentlyContinue) { return 'mill' }
        [Console]::Error.WriteLine('no mill found - set MILL=C:\path\to\mill.exe, or install the VS Code')
        [Console]::Error.WriteLine('extension (it carries one) and use its Run button.')
        exit 1
    }

    $mill = Find-Mill

    # Judged on the exit code alone. A program that fails must be reported as
    # a failure, and a native program's stderr is not one - the same rule the
    # rest of this toolchain follows.
    function Invoke-Mill([string]$Program) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & $mill run $Program } finally { $ErrorActionPreference = $prev }
        exit $LASTEXITCODE
    }

    switch ($Command) {
        'run'  { Invoke-Mill 'spiro.shoddy' }
        'test' { Invoke-Mill 'test.shoddy' }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|test]')
            exit 2
        }
    }
}
finally { Pop-Location }
