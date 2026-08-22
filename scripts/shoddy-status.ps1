#!/usr/bin/env pwsh
# MAINTAINER TOOL - read-only. Where the work is in the release procedure.
#
#   ./scripts/shoddy-status.ps1            where the work is, and what comes next
#   ./scripts/shoddy-status.ps1 X.Y.Z      the same, and whether X.Y.Z is ready to ship
#
# THIS SCRIPT CHANGES NOTHING. Everything else in scripts/ mutates - branch
# cuts branches and deletes them, commit writes history, pr pushes, ship
# tags and pushes - so there was no way to ask where a piece of work had got
# to without moving it. The one place the whole picture was already
# assembled was the run of refusals at the top of shoddy-ship.ps1, and the only way
# to reach those was to ship. That is the hole this fills.
#
# THE ONE THING IT WRITES IS REMOTE-TRACKING REFS, because it fetches. Said
# out loud rather than buried: origin/* is updated, and nothing else is - no
# checkout, no index, no commit, no tag, no push. A status built on stale
# refs is worse than no status, because it is confidently wrong: local refs
# go stale the moment somebody else pushes and nothing announces it, which
# is why every script here fetches before claiming anything about origin. If
# the fetch fails the report still runs and says which lines are therefore
# local-only.
#
# IT IS A REPORT, NOT A GATE, and it exits 0 whatever it finds. A status
# tool that exits non-zero on incomplete work becomes a thing callers depend
# on as a gate, and then it has to be right about readiness rather than
# honest about state. shoddy-ship.ps1 is the gate; this only describes.
#
# "PROVEN" IS CI'S ANSWER, NOT THIS SCRIPT'S, and there is no local receipt
# behind it. There must not be: RELEASING.md retired the gate harness
# because a receipt keyed to yesterday's tree will happily print a pass, in
# a tenth of a second, describing work that no longer exists. CI can only
# speak about a commit it has been given, so on a branch that has never been
# pushed the honest answer is that nothing has been proved yet - which is
# exactly the state this is most often run in.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Version = ''
)
$ErrorActionPreference = 'Stop'

# The scripts live in scripts/; the repository they report on is one level up.
Set-Location (Split-Path -Parent $PSScriptRoot)

# git writes plenty to stderr that is not an error, and Windows PowerShell
# turns a native program's redirected stderr into NativeCommandError records
# the moment a caller captures both streams. Judge every call on its exit
# code alone. See shoddy-branch.ps1 for the full account.
function Gq {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
}
function Gtext { return "$(Gq @args)".Trim() }

function Row([string]$Label, [string]$Value, [string]$Color = 'Gray') {
    Write-Host ('  {0,-10}' -f $Label) -NoNewline
    Write-Host $Value -ForegroundColor $Color
}

Gq rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'STOPPED: not inside a git repository.' -ForegroundColor Red
    exit 1
}

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "STOPPED: '$Version' is not an exact X.Y.Z version." -ForegroundColor Red
    exit 1
}

# ---- fetch, so every claim about origin below is worth making ----
Write-Host '> git fetch --prune --tags' -ForegroundColor DarkGray
Gq fetch --prune --tags 2>&1 | Out-Null
$offline = ($LASTEXITCODE -ne 0)

Write-Host ''

# ---- the branch, and whether the tree is clean ----
$branch = Gtext rev-parse --abbrev-ref HEAD
$isWork = ($branch -like 'feature/*' -or $branch -like 'bug/*')

# ONE LINE PER CHANGE, and it has to be counted from the array git's output
# already is. Collapsing it with "$(...)" first - the obvious spelling, and
# what this did on its first run - joins every line with a SPACE, so two
# changed files report as one. The .sh twin is not exposed to this: command
# substitution there keeps the newlines.
$dirtyLines = @(Gq status --porcelain | Where-Object { "$_".Trim() })
$dirty = ($dirtyLines.Count -gt 0)

if ($dirty) {
    Row 'branch' "$branch  ($($dirtyLines.Count) uncommitted change(s))" 'Yellow'
} else {
    Row 'branch' "$branch  (clean)" 'Green'
}

# ---- how this branch sits against origin/main ----
$ahead = 0; $behind = 0
if (Gtext rev-parse --verify --quiet 'refs/remotes/origin/main') {
    $ahead = [int](Gtext rev-list --count 'origin/main..HEAD')
    $behind = [int](Gtext rev-list --count 'HEAD..origin/main')
    $desc = "$ahead ahead of origin/main, $behind behind"
    if ($behind -gt 0) { Row 'commits' "$desc  - ./scripts/shoddy-branch.ps1 sync brings them in" 'Yellow' }
    else { Row 'commits' $desc 'Gray' }
} else {
    Row 'commits' 'origin/main is unknown here' 'Yellow'
}

# ---- pushed? ----
$pushed = $false
if ($branch -ne 'main') {
    Gq show-ref --verify --quiet "refs/remotes/origin/$branch"
    $pushed = ($LASTEXITCODE -eq 0)
    if ($pushed) {
        $unpushed = [int](Gtext rev-list --count "origin/$branch..HEAD")
        if ($unpushed -gt 0) { Row 'pushed' "yes, but $unpushed commit(s) are not on origin yet" 'Yellow' }
        else { Row 'pushed' 'yes' 'Green' }
    } else {
        Row 'pushed' "no - origin/$branch does not exist" 'Yellow'
    }
}

# ---- gh answers the next two; without it, say so rather than guess ----
$gh = Get-Command gh -ErrorAction SilentlyContinue
function Ask([string[]]$GhArgs) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & gh @GhArgs 2>&1 | Out-String } finally { $ErrorActionPreference = $prev }
    return @{ Code = $LASTEXITCODE; Text = $out }
}

# ---- the pull request ----
$prState = ''
if ($isWork) {
    if (-not $gh) {
        Row 'review' 'gh is not installed, so the pull request was not checked' 'DarkGray'
    } elseif (-not $pushed) {
        Row 'review' 'no pull request - the branch is not pushed' 'Yellow'
    } else {
        $pr = Ask @('pr', 'view', $branch, '--json', 'state,url')
        if ($pr.Code -ne 0) {
            Row 'review' 'no pull request for this branch' 'Yellow'
        } else {
            if ($pr.Text -match '"state":"([A-Z]+)"') { $prState = $Matches[1] }
            $url = if ($pr.Text -match '"url":"([^"]+)"') { $Matches[1] } else { '' }
            $color = if ($prState -eq 'MERGED') { 'Green' } else { 'Yellow' }
            Row 'review' "$prState  $url" $color
        }
    }
}

# ---- proven? CI's answer, and only about a commit CI has been given ----
$commit = Gtext rev-parse HEAD
$short = $commit.Substring(0, 8)
$green = $false
if (-not $pushed -and $branch -ne 'main') {
    Row 'proven' "NO - nothing pushed, so CI has never seen $short" 'Red'
} elseif (-not $gh) {
    Row 'proven' 'gh is not installed, so CI was not asked' 'DarkGray'
} else {
    $ci = Ask @('run', 'list', '--commit', $commit, '--json', 'conclusion,name,status')
    if ($ci.Code -ne 0) {
        Row 'proven' "could not ask GitHub about $short" 'DarkGray'
    } elseif ($ci.Text -match '"conclusion":"failure"') {
        Row 'proven' "NO - CI is red on $short" 'Red'
    } elseif ($ci.Text -match '"status":"(in_progress|queued)"') {
        Row 'proven' "not yet - CI is still running on $short" 'Yellow'
    } elseif ($ci.Text -match '"conclusion":"success"') {
        $green = $true
        Row 'proven' "yes - CI is green on $short" 'Green'
    } else {
        Row 'proven' "no finished CI run for $short" 'Yellow'
    }
}

# ---- the notes, and the tag they are named for ----
$notesOk = $false
$tagFree = $false
if ($Version) {
    $tag = "v$Version"
    $notes = "release-notes/$tag.md"
    if (-not (Test-Path $notes)) {
        Row 'notes' "$notes does not exist" 'Yellow'
    } else {
        Gq ls-files --error-unmatch $notes > $null 2>&1
        if ($LASTEXITCODE -eq 0) {
            $notesOk = $true
            Row 'notes' "$notes  committed" 'Green'
        } else {
            Row 'notes' "$notes  NOT COMMITTED - release.yml reads the tagged commit" 'Yellow'
        }
    }

    Gq rev-parse --verify --quiet "refs/tags/$tag" > $null
    $localTag = ($LASTEXITCODE -eq 0)
    $remoteTag = [bool](Gtext ls-remote --tags origin "refs/tags/$tag")
    if ($localTag -or $remoteTag) {
        $where = if ($remoteTag) { 'on origin' } else { 'locally' }
        Row 'tag' "$tag already exists $where - a tag is never moved" 'Red'
    } else {
        $tagFree = $true
        Row 'tag' "$tag is free" 'Green'
    }
}

$previous = Gtext describe --tags --abbrev=0
if ($previous) { Row 'released' "$previous is the latest tag reachable from here" 'Gray' }

if ($offline) {
    Write-Host ''
    Write-Host '  NOTE: the fetch failed, so every line about origin is from local refs' -ForegroundColor Yellow
    Write-Host '        and may be stale. Nothing else about this report is affected.' -ForegroundColor Yellow
}

# ---- what comes next ----
# The order matches WORKFLOW.md: branch, work, prove, notes, review, land,
# ship. Only the first unmet thing is worth printing; a list of everything
# outstanding is a list nobody reads.
Write-Host ''
$next = if ($dirty) {
    './scripts/shoddy-commit.ps1 -Message "what changed"'
} elseif ($branch -eq 'main') {
    if (-not $Version) { 'pass a version to see whether it is ready: ./scripts/shoddy-status.ps1 X.Y.Z' }
    elseif (-not $notesOk) { "write release-notes/v$Version.md on a branch, and commit it" }
    elseif (-not $tagFree) { 'pick the next version - that tag is taken' }
    elseif (-not $green) { 'wait for CI to go green on main' }
    else { "./scripts/shoddy-ship.ps1 $Version" }
} elseif (-not $isWork) {
    "'$branch' is neither main nor feature/* nor bug/* - cut one with ./scripts/shoddy-branch.ps1"
} elseif ($ahead -eq 0) {
    'nothing to review yet - do the work, then ./scripts/shoddy-commit.ps1'
} elseif (-not $pushed -or -not $prState) {
    './build.ps1 test  then  ./build.ps1 check  then  ./scripts/shoddy-pr.ps1'
} elseif ($prState -eq 'MERGED') {
    './scripts/shoddy-branch.ps1 land'
} elseif ($green) {
    'merge the pull request on GitHub - that step is a person, not a script'
} else {
    'wait for CI on the pull request'
}

Write-Host '  next      ' -NoNewline
Write-Host $next -ForegroundColor Cyan
Write-Host ''
exit 0
