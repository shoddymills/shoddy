#!/usr/bin/env pwsh
# Build / run the tally mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1 run [SPEC]       read the spec, print the report, show the chart
#   ./build.ps1 capture [SPEC]   the same, with nothing on screen - the PNG is the output
#   ./build.ps1 test             the headless suite: no window, no display
#   ./build.ps1 build            weave the program into bin/
#   ./build.ps1 clean            remove bin/
#
# run and capture are LIVE: they launch from wherever you actually typed
# .\build.ps1, not from this directory. SPEC defaults to this mill's own
# files/grades.spec if you don't give one; a SPEC you do name resolves
# against your own shell, same as any ordinary file argument would.
# Paths INSIDE a spec (data.file, window.capture) are relative to
# wherever the spec itself resolved from - the shipped default spec
# names files/... to match sitting beside it.
#
# capture passes --no-window, which opens every scribbler hidden and stops
# windows outliving the program. Pair it with window.show = no in the spec:
# the flag says "put nothing on screen", the spec key says "do not wait for
# anyone to dismiss it". A spec that shows, run with --no-window, would
# otherwise be waiting on a window nobody can see.
#
# The test target needs NOTHING - no display, no network. Everything
# between a file's text and a finished report is pure, which is the whole
# reason tally-core.shoddy and tally.shoddy are separate files.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run',
    [Parameter(Position = 1)][string]$Spec = ''
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

    $SpecPath = if ($Spec) { $Spec } else { Join-Path $MillDir 'files/grades.spec' }

    switch ($Command) {
        'run' {
            Assert-Mill
            Invoke-Live $OrigLocation { & $Mill run (Join-Path $MillDir 'tally.shoddy') $SpecPath }
            exit $LASTEXITCODE
        }
        'capture' {
            Assert-Mill
            Invoke-Live $OrigLocation { & $Mill run --no-window (Join-Path $MillDir 'tally.shoddy') $SpecPath }
            exit $LASTEXITCODE
        }
        'test' {
            # test.shoddy's own fixtureDir is repo-root-relative
            # (mills/tally/files/), not mill-relative, so this one runs
            # from the repo root rather than staying pinned to this
            # directory. $null on the pipeline is this shell's
            # `< /dev/null`.
            Assert-Mill
            Invoke-Live $Repo { $null | & $Mill run mills/tally/test.shoddy }
            exit $LASTEXITCODE
        }
        'build' {
            Invoke-Weave tally.shoddy
            Write-Host 'woven into bin/ - but note that a woven program has no window'
            Write-Host "backend: charts need 'mill run'. Reports and captures are fine."
        }
        'clean' {
            if (Test-Path bin) { Remove-Item -Recurse -Force bin }
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run SPEC|capture SPEC|test|build|clean]')
            exit 2
        }
    }

    exit 0
}
finally { Pop-Location }
