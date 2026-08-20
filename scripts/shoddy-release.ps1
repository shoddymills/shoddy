#!/usr/bin/env pwsh
# MAINTAINER TOOL - pushes to origin/main and creates tags. Assumes write access.
#
# scripts/shoddy-release.ps1 X.Y.Z [-Yes]   (run from the repo root)
#
# The whole release, from a clean repo, in one shot:
#   main fast-forwarded to origin/main -> release/VX.Y.Z cut -> ./build.ps1 all X.Y.Z
#   (clean + test + package; a red test stops everything) -> commit the two
#   version files ->
#   push branch -> tag vX.Y.Z -> push tag -> merge --no-ff into main -> push.
#
# Pushing the tag is what ships: the Release workflow rebuilds on a clean runner
# and publishes the GitHub Release with the .vsix attached. The local build here
# is the gate, not the artifact - nothing is pushed until it is green. The .vsix
# is build output and is never committed.
#
# Refuses to start unless every precondition holds: exact X.Y.Z version, repo root,
# clean tree, no merge in progress, branch/tag not already taken (local or origin).
# -Yes skips the confirmation prompt.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Version,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg) { Write-Host "RELEASE STOPPED: $Msg" -ForegroundColor Red; exit 1 }
function G {
    Write-Host ("> git " + ($args -join ' ')) -ForegroundColor Cyan
    # Hardening, not a fix for anything this script does wrong on its own.
    # git says perfectly ordinary things on stderr - "Already on 'main'",
    # "Switched to branch", the push summary. Run plainly that is harmless,
    # but the moment a CALLER redirects (.\shoddy-release.ps1 1.0.0 2>&1 | tee
    # log.txt, or a CI step that captures both streams) Windows PowerShell
    # wraps each stderr line in a NativeCommandError, and with
    # $ErrorActionPreference = 'Stop' that terminates the release halfway
    # through. The exit code is the only honest signal a native program
    # gives, so judge the call on that alone.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') failed (exit $LASTEXITCODE)." }
}
# G's twin for the calls below whose EXIT CODE is the question being asked
# rather than a failure - "is the tree dirty?", "does this tag exist?".
# Same stderr neutralisation as G, and needed for the same reason: these
# ran bare, so a single line on git's stderr could terminate the release
# during its own preconditions.
function Gq {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
}

# --- argument ---
if (-not $Version) { Fail "usage: scripts/shoddy-release.ps1 X.Y.Z [-Yes]   (e.g. scripts/shoddy-release.ps1 1.0.0)" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version must be exactly X.Y.Z, digits only (got '$Version')." }
$Branch = "release/V$Version"
$Tag    = "v$Version"
$Vsix   = "vscode-shoddy/vscode-shoddy-$Version.vsix"

# --- preconditions: right place, clean state ---
Gq rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) { Fail "not inside a git repository." }
# --show-prefix is empty exactly at the repo root, and it is the only form of
# this check that cannot be defeated by spelling. --show-toplevel answers
# C:/github/shoddy while Get-Location answers C:\github\shoddy, and swapping
# the slashes still leaves a case difference, a trailing separator, a
# substituted drive or a path reached through a link to disagree with a
# directory that is perfectly correct. The .sh twin learned this and wrote
# down why; this one never got the fix.
if ((Gq rev-parse --show-prefix)) { Fail "run from the repo root: $(Gq rev-parse --show-toplevel)" }
if (-not (Test-Path build.ps1) -or -not (Test-Path vscode-shoddy/package.json)) {
    Fail "this doesn't look like the shoddy repo root."
}
Gq diff --quiet
if ($LASTEXITCODE -ne 0) { Fail "unstaged changes present - commit or stash first (see git status)." }
Gq diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Fail "staged-but-uncommitted changes present - commit or unstage first." }
Gq rev-parse -q --verify MERGE_HEAD > $null
if ($LASTEXITCODE -eq 0) { Fail "a merge is in progress - finish or abort it first." }

# --- preconditions: name not taken anywhere ---
G fetch origin --tags --prune
Gq show-ref --verify --quiet "refs/heads/$Branch"
if ($LASTEXITCODE -eq 0) { Fail "branch $Branch already exists locally." }
Gq show-ref --verify --quiet "refs/remotes/origin/$Branch"
if ($LASTEXITCODE -eq 0) { Fail "branch $Branch already exists on origin." }
Gq show-ref --verify --quiet "refs/tags/$Tag"
if ($LASTEXITCODE -eq 0) { Fail "tag $Tag already exists locally." }
Gq ls-remote --exit-code --tags origin $Tag > $null
if ($LASTEXITCODE -eq 0) { Fail "tag $Tag already exists on origin." }

# --- confirm ---
Write-Host ""
Write-Host "Release plan for ${Version}:" -ForegroundColor Yellow
Write-Host "  main          -> fast-forwarded to origin/main"
Write-Host "  $Branch -> created; ./build.ps1 all $Version (clean + test + package)"
Write-Host "  commit + push -> package.json ('$Tag release')"
Write-Host "  tag + push    -> $Tag   (this is what triggers the Release workflow)"
Write-Host "  main          -> merge --no-ff $Branch, pushed"
Write-Host ""
$Notes = "release-notes/$Tag.md"
if (Test-Path $Notes) {
    Write-Host "  release notes -> $Notes ($((Get-Content $Notes).Count) lines)"
} else {
    Write-Host "  release notes -> MISSING: $Notes" -ForegroundColor Yellow
    Write-Host "                   The release body will fall back to the merge log."
    Write-Host "                   Notes must be committed BEFORE the tag: answer n,"
    Write-Host "                   write them, commit, then re-run. See release-notes/README.md."
}
if (-not $Yes) {
    $a = Read-Host "Proceed? (y/N)"
    if ($a -notmatch '^(y|yes)$') { Write-Host "aborted, nothing done."; exit 0 }
}

# --- full checkout: release always builds from up-to-date main ---
G checkout main
G pull --ff-only origin main
G checkout -b $Branch

# --- clear stale packages so the version check below can't pass on an old file ---
Get-ChildItem vscode-shoddy -Filter *.vsix -ErrorAction SilentlyContinue | Remove-Item -Force

# --- build + test + package; nothing has been pushed yet ---
& .\build.ps1 all $Version
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD/TEST FAILED - nothing was pushed." -ForegroundColor Red
    Write-Host "inspect, then undo with: git checkout main; git branch -D $Branch"
    exit 1
}
if (-not (Test-Path $Vsix)) { Fail "expected package $Vsix was not produced." }
$pkg = (Get-Content vscode-shoddy/package.json -Raw | ConvertFrom-Json).version
if ($pkg -ne $Version) { Fail "package.json version is '$pkg', expected '$Version'." }

# The compiled binaries carry their version from Directory.Build.props, not
# from package.json, so it is bumped here too. Without this the mill, sparky
# and fettle would keep reporting the PREVIOUS release while their archives
# were named for this one - which is the state that made a hardcoded 1.0.0
# survive unnoticed through 2.2.0.
$props = Get-Content Directory.Build.props -Raw
$bumped = $props -replace '<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>', "<Version>$Version</Version>"
if ($bumped -eq $props -and $props -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    Fail "could not find a <Version> element to bump in Directory.Build.props."
}
# NOT Set-Content -Encoding utf8. That means UTF-8 WITH a byte-order mark
# under Windows PowerShell 5.1 and WITHOUT one under PowerShell 7, so the
# same script wrote two different files depending on which host happened to
# launch it - and the gate driver launched it with the one that adds the
# mark. Written explicitly so the answer does not depend on the shell.
[System.IO.File]::WriteAllText(
    (Join-Path (Get-Location).Path 'Directory.Build.props'),
    $bumped,
    (New-Object System.Text.UTF8Encoding($false)))

# --- publish: the version bump is the only artifact that belongs in the commit ---
G add vscode-shoddy/package.json Directory.Build.props
G commit -m "$Tag release"
G push -u origin $Branch
G tag $Tag
G push origin $Tag
G checkout main
G merge --no-ff $Branch -m "Merge branch '$Branch'"
G push origin main

Write-Host ""
Write-Host "DONE - $Tag is tagged and merged." -ForegroundColor Green
Write-Host "The Release workflow is now building $Tag and will publish the GitHub Release"
Write-Host "with vscode-shoddy-$Version.vsix attached. Watch it in the Actions tab; if it fails,"
Write-Host "the tag is already public, so fix forward with a new patch version."
Write-Host "Release branch kept as hotfix base: $Branch"
Write-Host "  (delete anytime: git branch -d $Branch; git push origin --delete $Branch)"
