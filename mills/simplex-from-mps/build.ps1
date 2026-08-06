#!/usr/bin/env pwsh
# Build / run the simplex-from-mps mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1            build the program into bin/
#   ./build.ps1 build      same as above
#   ./build.ps1 run FILE   build if needed, then run on an MPS FILE
#   ./build.ps1 test       solve both fixtures and check the answers
#   ./build.ps1 clean      remove built binaries from bin/
#
# The build weaves simplex-mps.shoddy to a self-contained assembly and
# drops every binary (the program, its runtimeconfig, Shoddy.Runtime.dll)
# into bin/. To just run an already-built program, no rebuild:
#
#   dotnet bin/simplex-mps.dll files/blend.mps        # or files/mix.mps
#
# Add -z (or --zero-lower) to force x >= 0 on every variable.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'build',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest
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
    $Src = 'simplex-mps.shoddy'
    $Out = 'bin/simplex-mps.dll'

    function Assert-Mill {
        if (-not (Test-Path $Mill)) {
            Write-Host "mill toolchain not built; building it into $Repo/bin ..."
            dotnet publish (Join-Path $Repo 'src/Shoddy.Mill') -c Release -o (Join-Path $Repo 'bin')
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    # The weave drops its output beside the source; this moves it into bin/,
    # the same shuffle the .sh does with mv -f. Shoddy.*.dll may or may not be
    # there depending on what the weave decided to copy, so its absence is not
    # an error.
    function Invoke-BuildMill {
        Assert-Mill
        & $Mill weave $Src
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        New-Item -ItemType Directory -Path bin -Force | Out-Null
        Move-Item -Force simplex-mps.dll, simplex-mps.runtimeconfig.json bin/
        $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
        if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
        Write-Host "built -> $Out"
    }

    switch ($Command) {
        'build' {
            Invoke-BuildMill
        }
        'run' {
            if (-not $Rest -or $Rest.Count -lt 1) {
                [Console]::Error.WriteLine('usage: ./build.ps1 run FILE.mps [-z]')
                exit 2
            }
            if (-not (Test-Path $Out)) { Invoke-BuildMill }
            & dotnet $Out @Rest
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            & $Mill run test.shoddy
            exit $LASTEXITCODE
        }
        'clean' {
            $junk = @(Get-ChildItem bin/*.dll, bin/*.json -ErrorAction SilentlyContinue)
            if ($junk.Count -gt 0) { Remove-Item -Force $junk }
            Write-Host 'cleaned.'
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [build|run FILE.mps|test|clean]')
            exit 2
        }
    }

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
