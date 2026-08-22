#!/usr/bin/env bash
# MAINTAINER TOOL - read-only. Where the work is in the release procedure.
#
#   ./scripts/shoddy-status.sh            where the work is, and what comes next
#   ./scripts/shoddy-status.sh X.Y.Z      the same, and whether X.Y.Z is ready to ship
#
# The twin of shoddy-status.ps1 and equivalent to it.
#
# THIS SCRIPT CHANGES NOTHING. Everything else in scripts/ mutates - branch
# cuts branches and deletes them, commit writes history, pr pushes, ship
# tags and pushes - so there was no way to ask where a piece of work had got
# to without moving it. The one place the whole picture was already
# assembled was the run of refusals at the top of shoddy-ship.sh, and the only way
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
# honest about state. shoddy-ship.sh is the gate; this only describes.
#
# "PROVEN" IS CI'S ANSWER, NOT THIS SCRIPT'S, and there is no local receipt
# behind it. There must not be: RELEASING.md retired the gate harness
# because a receipt keyed to yesterday's tree will happily print a pass, in
# a tenth of a second, describing work that no longer exists. CI can only
# speak about a commit it has been given, so on a branch that has never been
# pushed the honest answer is that nothing has been proved yet - which is
# exactly the state this is most often run in.

# NO -e HERE, DELIBERATELY, where every other script in scripts/ has it.
# Nearly every command below is a QUESTION - is there a pull request, is
# that tag taken, has CI finished - and a non-zero answer is the
# information, not a failure. Under -e the first "no" would kill the report
# halfway through, which is the one thing a status tool must never do.
set -uo pipefail

# The scripts live in scripts/; the repository they report on is one level up.
cd "$(dirname "$0")/.."

version=${1:-}

row() { printf '  %-10s%s\n' "$1" "$2"; }

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || {
    echo "STOPPED: not inside a git repository." >&2; exit 1; }

if [ -n "$version" ] && ! printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "STOPPED: '$version' is not an exact X.Y.Z version." >&2
    exit 1
fi

# ---- fetch, so every claim about origin below is worth making ----
echo "> git fetch --prune --tags"
offline=no
git fetch --prune --tags >/dev/null 2>&1 || offline=yes

echo

# ---- the branch, and whether the tree is clean ----
branch=$(git rev-parse --abbrev-ref HEAD)
dirty=$(git status --porcelain)
is_work=no
case "$branch" in feature/*|bug/*) is_work=yes ;; esac

if [ -n "$dirty" ]; then
    row "branch" "$branch  ($(printf '%s\n' "$dirty" | wc -l | tr -d ' ') uncommitted change(s))"
else
    row "branch" "$branch  (clean)"
fi

# ---- how this branch sits against origin/main ----
ahead=0
behind=0
if git rev-parse --verify --quiet refs/remotes/origin/main >/dev/null; then
    ahead=$(git rev-list --count origin/main..HEAD)
    behind=$(git rev-list --count HEAD..origin/main)
    if [ "$behind" != "0" ]; then
        row "commits" "$ahead ahead of origin/main, $behind behind  - ./scripts/shoddy-branch.sh sync brings them in"
    else
        row "commits" "$ahead ahead of origin/main, $behind behind"
    fi
else
    row "commits" "origin/main is unknown here"
fi

# ---- pushed? ----
pushed=no
if [ "$branch" != "main" ]; then
    if git show-ref --verify --quiet "refs/remotes/origin/$branch"; then
        pushed=yes
        unpushed=$(git rev-list --count "origin/$branch..HEAD")
        if [ "$unpushed" != "0" ]; then
            row "pushed" "yes, but $unpushed commit(s) are not on origin yet"
        else
            row "pushed" "yes"
        fi
    else
        row "pushed" "no - origin/$branch does not exist"
    fi
fi

have_gh=no
command -v gh >/dev/null 2>&1 && have_gh=yes

# ---- the pull request ----
pr_state=""
if [ "$is_work" = "yes" ]; then
    if [ "$have_gh" = "no" ]; then
        row "review" "gh is not installed, so the pull request was not checked"
    elif [ "$pushed" = "no" ]; then
        row "review" "no pull request - the branch is not pushed"
    elif pr=$(gh pr view "$branch" --json state,url 2>/dev/null); then
        pr_state=$(printf '%s' "$pr" | sed -n 's/.*"state":"\([A-Z]*\)".*/\1/p')
        url=$(printf '%s' "$pr" | sed -n 's/.*"url":"\([^"]*\)".*/\1/p')
        row "review" "$pr_state  $url"
    else
        row "review" "no pull request for this branch"
    fi
fi

# ---- proven? CI's answer, and only about a commit CI has been given ----
commit=$(git rev-parse HEAD)
short=$(git rev-parse --short=8 HEAD)
green=no
if [ "$pushed" = "no" ] && [ "$branch" != "main" ]; then
    row "proven" "NO - nothing pushed, so CI has never seen $short"
elif [ "$have_gh" = "no" ]; then
    row "proven" "gh is not installed, so CI was not asked"
elif ci=$(gh run list --commit "$commit" --json conclusion,name,status 2>/dev/null); then
    case "$ci" in
        *'"conclusion":"failure"'*)                    row "proven" "NO - CI is red on $short" ;;
        *'"status":"in_progress"'*|*'"status":"queued"'*) row "proven" "not yet - CI is still running on $short" ;;
        *'"conclusion":"success"'*)                    green=yes; row "proven" "yes - CI is green on $short" ;;
        *)                                             row "proven" "no finished CI run for $short" ;;
    esac
else
    row "proven" "could not ask GitHub about $short"
fi

# ---- the notes, and the tag they are named for ----
notes_ok=no
tag_free=no
if [ -n "$version" ]; then
    tag="v$version"
    notes="release-notes/$tag.md"
    if [ ! -f "$notes" ]; then
        row "notes" "$notes does not exist"
    elif git ls-files --error-unmatch "$notes" >/dev/null 2>&1; then
        notes_ok=yes
        row "notes" "$notes  committed"
    else
        row "notes" "$notes  NOT COMMITTED - release.yml reads the tagged commit"
    fi

    local_tag=no
    remote_tag=no
    git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null && local_tag=yes
    [ -n "$(git ls-remote --tags origin "refs/tags/$tag" 2>/dev/null)" ] && remote_tag=yes
    if [ "$local_tag" = "yes" ] || [ "$remote_tag" = "yes" ]; then
        where=locally
        [ "$remote_tag" = "yes" ] && where="on origin"
        row "tag" "$tag already exists $where - a tag is never moved"
    else
        tag_free=yes
        row "tag" "$tag is free"
    fi
fi

previous=$(git describe --tags --abbrev=0 2>/dev/null)
[ -n "$previous" ] && row "released" "$previous is the latest tag reachable from here"

if [ "$offline" = "yes" ]; then
    echo
    echo "  NOTE: the fetch failed, so every line about origin is from local refs"
    echo "        and may be stale. Nothing else about this report is affected."
fi

# ---- what comes next ----
# The order matches WORKFLOW.md: branch, work, prove, notes, review, land,
# ship. Only the first unmet thing is worth printing; a list of everything
# outstanding is a list nobody reads.
echo
if [ -n "$dirty" ]; then
    next='./scripts/shoddy-commit.sh -Message "what changed"'
elif [ "$branch" = "main" ]; then
    if   [ -z "$version" ];      then next='pass a version to see whether it is ready: ./scripts/shoddy-status.sh X.Y.Z'
    elif [ "$notes_ok" = "no" ]; then next="write release-notes/v$version.md on a branch, and commit it"
    elif [ "$tag_free" = "no" ]; then next='pick the next version - that tag is taken'
    elif [ "$green" = "no" ];    then next='wait for CI to go green on main'
    else                              next="./scripts/shoddy-ship.sh $version"
    fi
elif [ "$is_work" = "no" ]; then
    next="'$branch' is neither main nor feature/* nor bug/* - cut one with ./scripts/shoddy-branch.sh"
elif [ "$ahead" = "0" ]; then
    next='nothing to review yet - do the work, then ./scripts/shoddy-commit.sh'
elif [ "$pushed" = "no" ] || [ -z "$pr_state" ]; then
    next='./build.sh test  then  ./build.sh check  then  ./scripts/shoddy-pr.sh'
elif [ "$pr_state" = "MERGED" ]; then
    next='./scripts/shoddy-branch.sh land'
elif [ "$green" = "yes" ]; then
    next='merge the pull request on GitHub - that step is a person, not a script'
else
    next='wait for CI on the pull request'
fi

printf '  %-10s%s\n' "next" "$next"
echo
exit 0
