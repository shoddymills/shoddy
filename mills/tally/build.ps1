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
# SPEC defaults to files/grades.spec. Paths INSIDE a spec (data.file,
# window.capture) are relative to the directory you run from, which the
# run target makes the repo root - so a spec written for ./build.ps1 says
# mills/tally/files/... The shipped specs do exactly that.
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
    [Parameter(Position = 1)][string]$Spec = 'mills/tally/files/grades.spec'
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
            Push-Location $Repo
            try { & bin/mill.exe run mills/tally/tally.shoddy $Spec }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'capture' {
            Assert-Mill
            Push-Location $Repo
            try { & bin/mill.exe run --no-window mills/tally/tally.shoddy $Spec }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            # $null on the pipeline is this shell's `< /dev/null`.
            Push-Location $Repo
            try { $null | & bin/mill.exe run mills/tally/test.shoddy }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'build' {
            Assert-Mill
            # weave writes BESIDE the source and has no -o flag; this moves the
            # result into bin/, the same shuffle the other weaving mills do.
            & $Mill weave tally.shoddy
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            New-Item -ItemType Directory -Path bin -Force | Out-Null
            Move-Item -Force tally.dll, tally.runtimeconfig.json bin/
            $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
            if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
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

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
