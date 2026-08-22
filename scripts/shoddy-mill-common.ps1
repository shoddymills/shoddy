# scripts/shoddy-mill-common.ps1 - shared plumbing for every mill's build.ps1.
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

# Run a scriptblock containing a native call with $ErrorActionPreference
# neutralised for its duration, then judge it on its EXIT CODE alone - the
# only honest signal a native program gives. Same reason as the Native
# helper at the top of the repo's own build.ps1: Windows PowerShell 5.1
# wraps every stderr line a native program writes in a NativeCommandError
# record whenever the CALLER captures our streams, and under 'Stop' that
# record is terminating. So mill's ordinary "machine x.shoddy is not built
# - building" notice killed the run on a freshly cleaned tree, while the
# identical command straight into a console was fine.
#
# It takes a SCRIPTBLOCK rather than a command and its arguments, because
# the calls here are not all plain: several pipe their standard input
# ("$null | & $Mill run test.shoddy" is this shell's `< /dev/null`, and
# halifax feeds a transcript in with Get-Content). Wrapping the whole
# pipeline keeps those exactly as they were.
#
# It lives here because every mill's build.ps1 dot-sources this file, so
# one definition covers all twelve, whether run from the repo's build.ps1
# or on their own.
function Invoke-Native {
    param([Parameter(Mandatory)][scriptblock]$Action)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Action } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Assert-Mill {
    if (-not (Test-Path $Mill)) {
        Write-Host "mill toolchain not built; building it into $Repo/bin ..."
        Invoke-Native { dotnet publish (Join-Path $Repo 'src/Shoddy.Mill') -c Release -o (Join-Path $Repo 'bin') }
    }
}

function Invoke-Live {
    param(
        [Parameter(Mandatory)]$Location,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    # $ErrorActionPreference relaxed for the same reason Invoke-Native does
    # it: what runs in here is a live native program, and a line it writes
    # to stderr must not be mistaken for a terminating error. The exit code
    # is left to the caller, every one of which already reads it with
    # `exit $LASTEXITCODE` on the next line.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location $Location
    try { & $Action }
    finally { Pop-Location; $ErrorActionPreference = $prev }
}

function Invoke-Weave {
    param([Parameter(Mandatory)][string[]]$Sources)
    Assert-Mill
    foreach ($src in $Sources) {
        Invoke-Native { & $Mill weave $src }
    }
    New-Item -ItemType Directory -Path bin -Force | Out-Null
    foreach ($src in $Sources) {
        $base = $src -replace '\.shoddy$', ''
        Move-Item -Force "$base.dll", "$base.runtimeconfig.json" bin/
    }
    $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
    if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
}
