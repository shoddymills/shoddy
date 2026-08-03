#!/usr/bin/env pwsh
# MAINTAINER TOOL - pushes to origin/main and deletes remote branches. Assumes write access.
# Run from the repo root.
#
# scripts/shoddy-feature.ps1 new NAME     create feature/NAME off up-to-date main and switch to it
# scripts/shoddy-feature.ps1 ship [-Yes]  push the current feature branch, merge --no-ff into main,
#                                         push main, then delete the branch (local + origin)
#
# Guards: clean tree required; 'new' refuses names already taken (local or origin);
# 'ship' only runs from a feature/* branch, refuses when there is nothing to merge,
# and on merge conflict aborts cleanly and puts you back on your branch.
# A leading 'feature/' on NAME is stripped, so both spellings work.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command,
    [Parameter(Position = 1)][string]$Name,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg) { Write-Host "STOPPED: $Msg" -ForegroundColor Red; exit 1 }
function G {
    Write-Host ("> git " + ($args -join ' ')) -ForegroundColor Cyan
    # Hardening, not a fix for anything this script does wrong on its own.
    # git says perfectly ordinary things on stderr - "Already on 'main'",
    # "Switched to branch", the push summary. Run plainly that is harmless,
    # but the moment a CALLER redirects (.\shoddy-feature.ps1 ship 2>&1 | tee
    # log.txt, or a CI step that captures both streams) Windows PowerShell
    # wraps each stderr line in a NativeCommandError, and with
    # $ErrorActionPreference = 'Stop' that terminates the run halfway
    # through. The exit code is the only honest signal a native program
    # gives, so judge the call on that alone.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') failed (exit $LASTEXITCODE)." }
}

git rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) { Fail "not inside a git repository." }
git diff --quiet
if ($LASTEXITCODE -ne 0) { Fail "unstaged changes present - commit or stash first (see git status)." }
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Fail "staged-but-uncommitted changes present - commit or unstage first." }
git rev-parse -q --verify MERGE_HEAD > $null
if ($LASTEXITCODE -eq 0) { Fail "a merge is in progress - finish or abort it first." }

switch ($Command) {

    'new' {
        if (-not $Name) { Fail "usage: scripts/shoddy-feature.ps1 new NAME" }
        $Name = $Name -replace '^feature/', ''
        if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
            Fail "branch name may use letters, digits, . _ - only (got '$Name')."
        }
        $Branch = "feature/$Name"
        G fetch origin --prune
        git show-ref --verify --quiet "refs/heads/$Branch"
        if ($LASTEXITCODE -eq 0) { Fail "$Branch already exists locally." }
        git show-ref --verify --quiet "refs/remotes/origin/$Branch"
        if ($LASTEXITCODE -eq 0) { Fail "$Branch already exists on origin." }
        G checkout main
        G pull --ff-only origin main
        G checkout -b $Branch
        Write-Host ""
        Write-Host "On $Branch (cut from up-to-date main)." -ForegroundColor Green
        Write-Host "Work, commit, then: scripts/shoddy-feature.ps1 ship"
    }

    'ship' {
        $Branch = git rev-parse --abbrev-ref HEAD
        if ($Branch -notlike 'feature/*') {
            Fail "current branch is '$Branch' - ship only runs from a feature/* branch."
        }
        G fetch origin --prune
        $count = [int](git rev-list --count origin/main..HEAD)
        if ($count -eq 0) { Fail "no commits on $Branch beyond origin/main - nothing to merge." }
        Write-Host ""
        Write-Host "Will merge these $count commit(s) from $Branch into main, then delete the branch:" -ForegroundColor Yellow
        git log --oneline origin/main..HEAD
        if (-not $Yes) {
            $a = Read-Host "Proceed? (y/N)"
            if ($a -notmatch '^(y|yes)$') { Write-Host "aborted, nothing done."; exit 0 }
        }
        Write-Host "> git push -u origin $Branch" -ForegroundColor Cyan
        git push -u origin $Branch
        if ($LASTEXITCODE -ne 0) {
            Fail "push rejected - origin/$Branch has commits you don't have. git pull, then re-run ship."
        }
        G checkout main
        G pull --ff-only origin main
        Write-Host "> git merge --no-ff $Branch" -ForegroundColor Cyan
        git merge --no-ff $Branch -m "Merge branch '$Branch'"
        if ($LASTEXITCODE -ne 0) {
            git merge --abort
            git checkout $Branch
            Fail "merge conflicts with main. On $Branch run: git merge main (resolve, commit), then re-run ship."
        }
        G push origin main
        G branch -d $Branch
        G push origin --delete $Branch
        Write-Host ""
        Write-Host "DONE - $Branch merged into main and deleted (local + origin)." -ForegroundColor Green
    }

    default {
        Write-Host "usage: scripts/shoddy-feature.ps1 new NAME   create feature/NAME off up-to-date main"
        Write-Host "       scripts/shoddy-feature.ps1 ship [-Yes] push current feature branch, merge --no-ff into main, delete it"
        exit 2
    }
}
