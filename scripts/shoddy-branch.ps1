#!/usr/bin/env pwsh
# MAINTAINER TOOL - pushes to origin/main and deletes remote branches. Assumes write access.
# Run from the repo root.
#
# scripts/shoddy-branch.ps1 feature NAME   create feature/NAME off up-to-date main and switch to it
# scripts/shoddy-branch.ps1 bug NAMENN     create bug/NAMENN off up-to-date main and switch to it
# scripts/shoddy-branch.ps1 ship [-Yes]    push the current feature/bug branch, merge --no-ff into
#                                          main, push main, then delete the branch (local + origin)
#
# Guards: clean tree required; 'feature' and 'bug' refuse names already taken (local or origin);
# a bug name must end in two digits, numbered per origin feature (bug/pudsey01 is the first fix
# to work that shipped from feature/pudsey); 'ship' only runs from a feature/* or bug/* branch,
# refuses when there is nothing to merge, and on merge conflict aborts cleanly and puts you back
# on your branch.
# A leading 'feature/' or 'bug/' on NAME is stripped, so both spellings work.
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
    # but the moment a CALLER redirects (.\shoddy-branch.ps1 ship 2>&1 | tee
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
# G's twin for the calls whose EXIT CODE this script wants to INSPECT
# rather than treat as fatal: the preconditions below, where a non-zero
# answer is the question being asked ("is the tree dirty?"), and the two
# mutating calls that carry their own tailored recovery. Same stderr
# neutralisation as G and for exactly the same reason - without it,
# `git push`, which writes its progress to stderr every single time,
# terminated ship before its own error check could run.
function Gq {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
}

# The one cut, shared by 'feature' and 'bug': refuse a taken name, then
# branch off an up-to-date main. The prefix is the only thing the two
# verbs disagree about.
function Cut([string]$Branch) {
    G fetch origin --prune
    Gq show-ref --verify --quiet "refs/heads/$Branch"
    if ($LASTEXITCODE -eq 0) { Fail "$Branch already exists locally." }
    Gq show-ref --verify --quiet "refs/remotes/origin/$Branch"
    if ($LASTEXITCODE -eq 0) { Fail "$Branch already exists on origin." }
    G checkout main
    G pull --ff-only origin main
    G checkout -b $Branch
    Write-Host ""
    Write-Host "On $Branch (cut from up-to-date main)." -ForegroundColor Green
    Write-Host "Work, commit, then: scripts/shoddy-branch.ps1 ship"
}

Gq rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) { Fail "not inside a git repository." }
Gq diff --quiet
if ($LASTEXITCODE -ne 0) { Fail "unstaged changes present - commit or stash first (see git status)." }
Gq diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Fail "staged-but-uncommitted changes present - commit or unstage first." }
Gq rev-parse -q --verify MERGE_HEAD > $null
if ($LASTEXITCODE -eq 0) { Fail "a merge is in progress - finish or abort it first." }

switch ($Command) {

    'feature' {
        if (-not $Name) { Fail "usage: scripts/shoddy-branch.ps1 feature NAME" }
        $Name = $Name -replace '^feature/', ''
        if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
            Fail "branch name may use letters, digits, . _ - only (got '$Name')."
        }
        Cut "feature/$Name"
    }

    'bug' {
        if (-not $Name) { Fail "usage: scripts/shoddy-branch.ps1 bug NAMENN   (bug/<origin-feature>NN, e.g. bug pudsey01)" }
        $Name = $Name -replace '^bug/', ''
        if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
            Fail "branch name may use letters, digits, . _ - only (got '$Name')."
        }
        # Numbered per origin feature, and the number is not optional: the
        # name says which shipped feature the fix belongs to and which fix
        # it is, so bug/pudsey refuses where bug/pudsey01 is meant.
        if ($Name -notmatch '\d\d$') {
            Fail "a bug branch is bug/<origin-feature>NN, numbered per origin feature (got '$Name'). Taken numbers: git --no-pager log --oneline main --grep=`"Merge branch 'bug/`""
        }
        Cut "bug/$Name"
    }

    'ship' {
        $Branch = Gq rev-parse --abbrev-ref HEAD
        # bug/* ships the same way: a fix branch (bug/<origin-feature>NN)
        # merges and deletes exactly as a feature does. Everything else
        # still refuses.
        if ($Branch -notlike 'feature/*' -and $Branch -notlike 'bug/*') {
            Fail "current branch is '$Branch' - ship only runs from a feature/* or bug/* branch."
        }
        G fetch origin --prune
        $count = [int](Gq rev-list --count origin/main..HEAD)
        if ($count -eq 0) { Fail "no commits on $Branch beyond origin/main - nothing to merge." }
        Write-Host ""
        Write-Host "Will merge these $count commit(s) from $Branch into main, then delete the branch:" -ForegroundColor Yellow
        Gq log --oneline origin/main..HEAD
        if (-not $Yes) {
            $a = Read-Host "Proceed? (y/N)"
            if ($a -notmatch '^(y|yes)$') { Write-Host "aborted, nothing done."; exit 0 }
        }
        Write-Host "> git push -u origin $Branch" -ForegroundColor Cyan
        Gq push -u origin $Branch
        if ($LASTEXITCODE -ne 0) {
            Fail "push rejected - origin/$Branch has commits you don't have. git pull, then re-run ship."
        }
        G checkout main
        G pull --ff-only origin main
        Write-Host "> git merge --no-ff $Branch" -ForegroundColor Cyan
        Gq merge --no-ff $Branch -m "Merge branch '$Branch'"
        if ($LASTEXITCODE -ne 0) {
            Gq merge --abort
            Gq checkout $Branch
            Fail "merge conflicts with main. On $Branch run: git merge main (resolve, commit), then re-run ship."
        }
        G push origin main
        G branch -d $Branch
        G push origin --delete $Branch
        Write-Host ""
        Write-Host "DONE - $Branch merged into main and deleted (local + origin)." -ForegroundColor Green
    }

    default {
        Write-Host "usage: scripts/shoddy-branch.ps1 feature NAME   create feature/NAME off up-to-date main"
        Write-Host "       scripts/shoddy-branch.ps1 bug NAMENN     create bug/NAMENN off up-to-date main"
        Write-Host "       scripts/shoddy-branch.ps1 ship [-Yes]    push current feature/bug branch, merge --no-ff into main, delete it"
        exit 2
    }
}
