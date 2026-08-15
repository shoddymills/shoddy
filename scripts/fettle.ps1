#!/usr/bin/env pwsh
# Fettler against THIS repository, with the roots already declared.
#
#   scripts/fettle.ps1 find "fettler/**/*.cs"
#   scripts/fettle.ps1 search "^public sealed" --glob "fettler/**/*.cs" --json
#   scripts/fettle.ps1 roots
#
# This is what R3.3 is for. The logic lives in the program, so this file
# and its .sh twin hold no logic at all and cannot drift from each other.
#
# The roots are ABSOLUTE, computed from this script's own location rather
# than from wherever it was invoked. A relative root would bind the
# boundary to the caller's current directory, so the same command would
# mean different things from different places - and a caller that has to
# keep correcting its working directory has gone back to doing shell work
# by another name.
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$ErrorActionPreference = 'Stop'

$repo  = Split-Path -Parent $PSScriptRoot
$notes = Join-Path (Split-Path -Parent $repo) 'shoddy-planning'

# Whichever fettle this checkout has, in the order a maintainer wants it:
# the published single-file binary, then a release build, then a debug
# build, then whatever is on PATH. Named candidates rather than a guess,
# and a refusal that says what it looked for.
$candidates = @(
    (Join-Path $repo 'artifacts/publish/fettle/win-x64/fettle.exe'),
    (Join-Path $repo 'artifacts/bin/fettle/release/fettle.exe'),
    (Join-Path $repo 'artifacts/bin/fettle/debug/fettle.exe')
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    if (Get-Command fettle -ErrorAction SilentlyContinue) { $exe = 'fettle' }
    else {
        Write-Host 'STOPPED: no fettle found. Build it with fettler/build.ps1, or put it on PATH.' -ForegroundColor Red
        $candidates | ForEach-Object { Write-Host "  looked in: $_" }
        exit 1
    }
}


$roots = @('--root', "repo=$repo")
if (Test-Path $notes) { $roots += @('--root', "notes=$notes") }

# Judged on its exit code alone: fettle writes its complete answer to
# stdout (R3.7), and Windows PowerShell turns a native program's
# redirected stderr into NativeCommandError records.
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { & $exe @roots @Rest } finally { $ErrorActionPreference = $prev }
exit $LASTEXITCODE
