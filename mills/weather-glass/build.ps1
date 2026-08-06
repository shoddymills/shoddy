#!/usr/bin/env pwsh
# Build / run the weather-glass mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1 run 63011   fetch and draw the report for a US ZIP
#   ./build.ps1 test        the offline suite - no network, no --allow-net
#   ./build.ps1 build       weave the program into bin/
#   ./build.ps1 clean       remove bin/
#
# The run target needs --allow-net: four HTTPS GETs, to api.zippopotam.us
# for the ZIP and to api.weather.gov for the forecast. Both get a
# User-Agent, because the weather service answers 403 without one.
#
# The test target needs NOTHING. Everything between a raw response and a
# finished row is pure, so the captured responses in files/ go through
# the core and the result is compared line by line against
# files/expected.out. That is the whole reason for the core/shell split.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run',
    [Parameter(Position = 1)][string]$Zip = '63011'
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
            try { & bin/mill.exe run --allow-net mills/weather-glass/weather-glass.shoddy $Zip }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            # $null on the pipeline is this shell's `< /dev/null`.
            Push-Location $Repo
            try { $null | & bin/mill.exe run mills/weather-glass/test.shoddy }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'build' {
            Assert-Mill
            # weave writes BESIDE the source and has no -o flag; this moves the
            # result into bin/, the same shuffle the other weaving mills do.
            & $Mill weave weather-glass.shoddy
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            New-Item -ItemType Directory -Path bin -Force | Out-Null
            Move-Item -Force weather-glass.dll, weather-glass.runtimeconfig.json bin/
            $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
            if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
            Write-Host 'woven into bin/ - run with: dotnet bin/weather-glass.dll 63011 (needs SHODDY_ALLOW_NET=1)'
        }
        'clean' {
            if (Test-Path bin) { Remove-Item -Recurse -Force bin }
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run ZIP|test|build|clean]')
            exit 2
        }
    }

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
