# scripts/mill-common.ps1 - shared plumbing for every mill's build.ps1.
#
# Dot-source this after Push-Location $PSScriptRoot, with $MillDir set to
# $PSScriptRoot of the CALLING script. Provides:
#
#   $Repo, $Mill    the toolchain, resolved from $MillDir
#   Assert-Mill     builds the toolchain into $Repo/bin if missing
#   Invoke-Live     runs a scriptblock from a given location, not from
#                   the mill's folder - for any target that launches a
#                   program a person interacts with, so a path they type
#                   or pass at the prompt resolves where THEY are
#                   sitting. Callers pass $OrigLocation (captured before
#                   Push-Location $PSScriptRoot, in every build.ps1's
#                   two-line preamble) explicitly, rather than this file
#                   reaching for a variable it hopes the caller set -
#                   that dependency belongs in the call, not in scope
#                   lookup across a dot-sourced file.
#   Invoke-Weave    weaves one or more .shoddy sources (found in
#                   $MillDir) and moves every resulting
#                   .dll/.runtimeconfig.json, plus any Shoddy.*.dll,
#                   into $MillDir/bin
#
# The rule this buys: run/capture/train/demo - anything that puts a live
# program in front of you - go through Invoke-Live, so a path resolves
# against wherever you actually typed .\build.ps1. test/build/clean never
# do: they read the mill's own fixtures and write the mill's own build
# output, and moving those because of where you happened to invoke the
# script from would be the surprise, not the fix.

$Repo = Join-Path $MillDir '../..'
$Mill = Join-Path $Repo 'bin/mill.exe'

function Assert-Mill {
    if (-not (Test-Path $Mill)) {
        Write-Host "mill toolchain not built; building it into $Repo/bin ..."
        dotnet publish (Join-Path $Repo 'src/Shoddy.Mill') -c Release -o (Join-Path $Repo 'bin')
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

function Invoke-Live {
    param(
        [Parameter(Mandatory)]$Location,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Push-Location $Location
    try { & $Action }
    finally { Pop-Location }
}

function Invoke-Weave {
    param([Parameter(Mandatory)][string[]]$Sources)
    Assert-Mill
    foreach ($src in $Sources) {
        & $Mill weave $src
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    New-Item -ItemType Directory -Path bin -Force | Out-Null
    foreach ($src in $Sources) {
        $base = $src -replace '\.shoddy$', ''
        Move-Item -Force "$base.dll", "$base.runtimeconfig.json" bin/
    }
    $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
    if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
}
