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
    $MillDir = $PSScriptRoot
    . (Join-Path $MillDir '../../scripts/shoddy-mill-common.ps1')

    switch ($Command) {
        'run' {
            Assert-Mill
            Invoke-Native { & $Mill run --allow-net weather-glass.shoddy $Zip }
            exit $LASTEXITCODE
        }
        'test' {
            # $null on the pipeline is this shell's `< /dev/null`.
            Assert-Mill
            Invoke-Native { $null | & $Mill run test.shoddy }
            exit $LASTEXITCODE
        }
        'build' {
            Invoke-Weave weather-glass.shoddy
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

    exit 0
}
finally { Pop-Location }
