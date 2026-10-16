#!/usr/bin/env pwsh
# Cut a release: tag main and push the tag. CI does the rest.
#
#   ./scripts/shoddy-ship.ps1 X.Y.Z         tag vX.Y.Z on main and push it
#   ./scripts/shoddy-ship.ps1 X.Y.Z -Yes    the same, without the confirmation prompt
#
# THIS SCRIPT DOES EXACTLY TWO MUTATING THINGS: it creates a tag and it
# pushes that tag. Everything else it does is refuse. There is no version
# bump to commit, no release branch to cut, and nothing to merge back,
# because the version is not stored in a file - release.yml reads it from
# the tag and passes it to the build verbs (`build.sh all X.Y.Z`,
# `hosts/mcp/build.sh publish X.Y.Z`), the same verbs CI runs.
#
# That is the whole design, and it is a deliberate reaction to how this
# used to work. The version lived in two files, so shipping meant rewriting
# them, committing the rewrite, cutting release/VX.Y.Z, building, tagging,
# pushing the branch, pushing the tag, and merging back - eight operations,
# each of which could fail with the previous seven already done, and a
# documented recovery procedure for exactly that. Here the tag IS the
# version, so there is one operation and nothing to unwind.
#
# PUSHING THE TAG IS THE MOMENT IT SHIPS. A tag someone may have fetched
# is never moved: if the workflow fails, fix forward with a new patch
# version.
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)][string]$Version,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'

# The scripts live in scripts/; the repository they release is one level up.
Set-Location (Split-Path -Parent $PSScriptRoot)

function Stop-With([string]$Message, [string]$Remedy = '') {
    Write-Host "STOPPED: $Message" -ForegroundColor Red
    if ($Remedy) { Write-Host "         $Remedy" -ForegroundColor Yellow }
    exit 1
}

# git writes plenty to stderr that is not an error, and Windows PowerShell
# turns a native program's redirected stderr into NativeCommandError
# records. Judge every call on its exit code alone.
#
# NOT called Git. PowerShell resolves a command name as alias, then
# FUNCTION, then cmdlet, then external program, and case-insensitively -
# so a function called Git makes the `& git` below call THIS FUNCTION,
# which calls itself until the call depth runs out.
function RunGit([string[]]$GitArgs) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & git @GitArgs 2>&1 } finally { $ErrorActionPreference = $prev }
    return @{ Code = $LASTEXITCODE; Text = ($out | Out-String).Trim() }
}

# ---- 1. the version must be a version ----
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Stop-With "'$Version' is not an exact X.Y.Z version." `
              "Pre-release and build suffixes are not supported here."
}
$tag = "v$Version"
$notes = "release-notes/$tag.md"

# ---- 2. this must be a git repository, with no merge half-done ----
if ((RunGit @('rev-parse', '--git-dir')).Code -ne 0) { Stop-With "this is not a git repository." }
if (Test-Path (Join-Path (RunGit @('rev-parse', '--git-dir')).Text 'MERGE_HEAD')) {
    Stop-With "a merge is in progress." "Finish or abort it first."
}

# ---- 3. LOCAL STATE IS NOT EVIDENCE ABOUT REMOTE STATE. Fetch first. ----
# Every check below about what origin has is worthless without this: refs
# go stale the moment somebody else pushes, and nothing announces it.
Write-Host "> git fetch --prune --tags" -ForegroundColor Cyan
$fetch = RunGit @('fetch', '--prune', '--tags')
if ($fetch.Code -ne 0) { Stop-With "could not reach origin." $fetch.Text }

# ---- 4. on main, clean, and level with origin ----
$branch = (RunGit @('rev-parse', '--abbrev-ref', 'HEAD')).Text
if ($branch -ne 'main') {
    Stop-With "on '$branch', not main." "A release is cut from main. Merge your branch first."
}

if ((RunGit @('status', '--porcelain')).Text) {
    Stop-With "the working tree is dirty." "Commit or stash before tagging - the tag must name a commit that exists."
}

$counts = (RunGit @('rev-list', '--left-right', '--count', 'origin/main...main')).Text -split '\s+'
if ($counts[0] -ne '0') { Stop-With "main is $($counts[0]) commit(s) behind origin/main." "Pull first." }
if ($counts[1] -ne '0') { Stop-With "main is $($counts[1]) commit(s) ahead of origin/main." "Push first - CI has not seen these commits." }

$commit = (RunGit @('rev-parse', 'HEAD')).Text

# ---- 5. the tag must be free, locally AND on origin ----
if ((RunGit @('rev-parse', '--verify', '--quiet', "refs/tags/$tag")).Code -eq 0) {
    Stop-With "$tag already exists locally." "Pick the next version. A tag is never moved."
}
if ((RunGit @('ls-remote', '--tags', 'origin', "refs/tags/$tag")).Text) {
    Stop-With "$tag already exists on origin." "Pick the next version. A tag someone may have fetched is never moved."
}

# ---- 6. the notes must exist, and be COMMITTED ----
# release.yml checks out the tag and reads only what that commit contains,
# so notes written afterwards are invisible to it and the release body
# falls back to a list of merge subjects.
if (-not (Test-Path $notes)) {
    Stop-With "$notes does not exist." "The release body comes from this file. Write it, commit it, push it, then ship."
}
if ((RunGit @('ls-files', '--error-unmatch', $notes)).Code -ne 0) {
    Stop-With "$notes is not committed." "The workflow reads the tagged commit; an untracked file is not in it."
}

# ---- 7. CI must already be green on this exact commit ----
# The proof is CI's, not this script's. Re-running the suite here would be
# a second opinion from a machine that is not the one that builds the
# release, and the earlier design's local 'receipt store' is exactly the
# thing that once printed a pass describing yesterday's tree.
#
# ci.yml's runs on THIS commit, and only ci.yml's: a green Pages deploy is
# not a proof of anything. Every run must be finished and every one a
# success. No run, a run still going, a failed, cancelled or timed-out
# run, or no way to ask - each one stops here. Pushing the tag is the
# moment it ships, and an unproven commit is never tagged.
$short = $commit.Substring(0, 8)
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Stop-With "gh is not installed, so CI's verdict on $short cannot be read." `
              "Install the GitHub CLI and run 'gh auth login'. A release is never tagged unproven."
}
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { $json = & gh run list --workflow ci.yml --commit $commit --json status,conclusion 2>$null | Out-String }
finally { $ErrorActionPreference = $prev }
if ($LASTEXITCODE -ne 0) { Stop-With "could not ask GitHub about CI for $short." "Check 'gh auth status', then try again." }
# foreach enumerates the parsed array: Windows PowerShell 5.1's
# ConvertFrom-Json hands an array back as ONE object, so @() alone would
# count an empty list as one run.
$runs = @(foreach ($r in ($json | ConvertFrom-Json)) { $r })
if ($runs.Count -eq 0) { Stop-With "ci.yml has no run for $short." "CI runs on every push to main: wait for it to start and finish." }
if (@($runs | Where-Object { $_.status -ne 'completed' }).Count -gt 0) {
    Stop-With "CI is still running on $short." "Wait for it. The tag names a commit already proven."
}
$bad = @($runs | Where-Object { $_.conclusion -ne 'success' })
if ($bad.Count -gt 0) {
    Stop-With "CI did not pass on $short ($(($bad | ForEach-Object { $_.conclusion }) -join ', '))" `
              "Fix it on a branch and merge before tagging."
}
Write-Host "CI is green on $short." -ForegroundColor Green

# ---- 8. say what is about to happen, then ask ----
# The display suites cannot run in CI (they open a real window), so the one
# thing worth remembering here is said here: if anything windowed changed
# since the last release, ./build.ps1 prove (which runs them) before yes.
# Judged on the code: with no tag yet, describe's answer is an error
# message, and it must not be printed as a version.
$described = RunGit @('describe', '--tags', '--abbrev=0')
$previous = if ($described.Code -eq 0) { $described.Text } else { '' }
Write-Host ''
Write-Host "  tag        $tag" -ForegroundColor White
Write-Host "  commit     $($commit.Substring(0,8))  $((RunGit @('log','-1','--format=%s')).Text)"
Write-Host "  notes      $notes"
if ($previous) { Write-Host "  since      $previous" }
Write-Host "  publishes  the .vsix and one sparky archive per OS"
Write-Host "  displays   CI cannot run the display suites - ./build.ps1 prove runs them"
Write-Host ''
Write-Host "  Pushing the tag is the moment it ships, and a tag is never moved." -ForegroundColor Yellow
Write-Host ''

if (-not $Yes) {
    $answer = Read-Host "Tag and push? (y/N)"
    if ($answer -ne 'y' -and $answer -ne 'Y') { Write-Host "Nothing was pushed."; exit 0 }
}

# ---- 9. the two mutating operations ----
Write-Host "> git tag -a $tag" -ForegroundColor Cyan
$made = RunGit @('tag', '-a', $tag, '-m', "$tag")
if ($made.Code -ne 0) { Stop-With "could not create the tag." $made.Text }

Write-Host "> git push origin $tag" -ForegroundColor Cyan
$pushed = RunGit @('push', 'origin', $tag)
if ($pushed.Code -ne 0) {
    Stop-With "could not push the tag; it exists locally and nothing has shipped." `
              "Delete it with 'git tag -d $tag' and try again, or push it yourself."
}

Write-Host ''
Write-Host "$tag is public. Watch Actions -> Release." -ForegroundColor Green
