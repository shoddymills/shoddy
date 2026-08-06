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
            Assert-Mill
            # weave writes BESIDE the source and has no -o flag; this moves the
            # result into bin/, the same shuffle the other weaving mills do.
            & $Mill weave emley-moor.shoddy
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            New-Item -ItemType Directory -Path bin -Force | Out-Null
            Move-Item -Force emley-moor.dll, emley-moor.runtimeconfig.json bin/
            $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
            if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
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

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
