#!/usr/bin/env pwsh
# Build / run the emley-moor mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1            serve on http://127.0.0.1:8080/
#   ./build.ps1 run        same as above
#   ./build.ps1 test       the routing tests - no network, no --allow-net
#   ./build.ps1 build      weave the server to bin/
#   ./build.ps1 clean      remove bin/
#
# The server needs --allow-net, because the network is a gated capability
# and a web server is the most network a program can be. It binds the
# LOOPBACK only: reachable from this machine and nowhere else. Editing
# that to ListenOn("0.0.0.0", ...) puts a plaintext HTTP server written
# over a weekend on a public address, so do not.
#
# The test target needs nothing. Every route is graded by calling
# Respond(request) with fixture text, because the server is a pure
# function of the request and the socket is twenty lines beside it. That
# split is the point of the mill; the tests are what it buys.
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
    $MillDir = $PSScriptRoot
    . (Join-Path $MillDir '../../scripts/mill-common.ps1')

    switch ($Command) {
        'run' {
            Assert-Mill
            & $Mill run --allow-net emley-moor.shoddy
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            # $null on the pipeline is this shell's `< /dev/null`.
            $null | & $Mill run test.shoddy
            exit $LASTEXITCODE
        }
        'build' {
            Invoke-Weave emley-moor.shoddy
            Write-Host 'woven into bin/ - run with: dotnet bin/emley-moor.dll (needs SHODDY_ALLOW_NET=1)'
        }
        'clean' {
            if (Test-Path bin) { Remove-Item -Recurse -Force bin }
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|test|build|clean]')
            exit 2
        }
    }

    exit 0
}
finally { Pop-Location }
