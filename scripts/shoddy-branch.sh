#!/usr/bin/env bash
# MAINTAINER TOOL - pushes to origin/main and deletes remote branches. Assumes write access.
# Run from the repo root.
#
# scripts/shoddy-branch.sh feature NAME   create feature/NAME off up-to-date main and switch to it
# scripts/shoddy-branch.sh bug NAMENN     create bug/NAMENN off up-to-date main and switch to it
# scripts/shoddy-branch.sh ship [-y|-Yes]  push the current feature/bug branch, merge --no-ff into
#                                         main, push main, then delete the branch (local + origin)
#
# Guards: clean tree required; 'feature' and 'bug' refuse names already taken (local or origin);
# a bug name must end in two digits, numbered per origin feature (bug/pudsey01 is the first fix
# to work that shipped from feature/pudsey); 'ship' only runs from a feature/* or bug/* branch,
# refuses when there is nothing to merge, and on merge conflict aborts cleanly and puts you back
# on your branch.
# A leading 'feature/' or 'bug/' on NAME is stripped, so both spellings work.
set -u

fail() { echo "STOPPED: $*" >&2; exit 1; }
run()  { echo "> $*"; "$@" || fail "'$*' failed."; }

# The one cut, shared by 'feature' and 'bug': refuse a taken name, then
# branch off an up-to-date main. The prefix is the only thing the two
# verbs disagree about.
cut_branch() {
    BR="$1"
    run git fetch origin --prune
    git show-ref --verify --quiet "refs/heads/$BR" && fail "$BR already exists locally."
    git show-ref --verify --quiet "refs/remotes/origin/$BR" && fail "$BR already exists on origin."
    run git checkout main
    run git pull --ff-only origin main
    run git checkout -b "$BR"
    echo
    echo "On $BR (cut from up-to-date main)."
    echo "Work, commit, then: scripts/shoddy-branch.sh ship"
}

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "not inside a git repository."
git diff --quiet || fail "unstaged changes present - commit or stash first (see git status)."
git diff --cached --quiet || fail "staged-but-uncommitted changes present - commit or unstage first."
git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 && fail "a merge is in progress - finish or abort it first."

case "${1:-}" in

feature)
    NAME="${2:-}"
    [ -n "$NAME" ] || fail "usage: scripts/shoddy-branch.sh feature NAME"
    NAME="${NAME#feature/}"
    echo "$NAME" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]*$' || fail "branch name may use letters, digits, . _ - only (got '$NAME')."
    cut_branch "feature/$NAME"
    ;;

bug)
    NAME="${2:-}"
    [ -n "$NAME" ] || fail "usage: scripts/shoddy-branch.sh bug NAMENN   (bug/<origin-feature>NN, e.g. bug pudsey01)"
    NAME="${NAME#bug/}"
    echo "$NAME" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]*$' || fail "branch name may use letters, digits, . _ - only (got '$NAME')."
    # Numbered per origin feature, and the number is not optional: the
    # name says which shipped feature the fix belongs to and which fix
    # it is, so bug/pudsey refuses where bug/pudsey01 is meant.
    echo "$NAME" | grep -Eq '[0-9][0-9]$' || fail "a bug branch is bug/<origin-feature>NN, numbered per origin feature (got '$NAME'). Taken numbers: git --no-pager log --oneline main --grep=\"Merge branch 'bug/\""
    cut_branch "bug/$NAME"
    ;;

ship)
    BR="$(git rev-parse --abbrev-ref HEAD)"
    case "$BR" in
        # bug/* ships the same way: a fix branch (bug/<origin-feature>NN)
        # merges and deletes exactly as a feature does. Everything else
        # still refuses.
        feature/*|bug/*) ;;
        *) fail "current branch is '$BR' - ship only runs from a feature/* or bug/* branch." ;;
    esac
    run git fetch origin --prune
    N="$(git rev-list --count origin/main..HEAD)"
    [ "$N" -gt 0 ] || fail "no commits on $BR beyond origin/main - nothing to merge."
    echo
    echo "Will merge these $N commit(s) from $BR into main, then delete the branch:"
    git log --oneline origin/main..HEAD
    # Scanned across every argument, and -Yes accepted, because that is what
    # the .ps1 twin calls this flag. The two spellings drifted apart while
    # WORKFLOW.md and CLAUDE.md both went on saying the twins take the same
    # arguments. They did not.
    YES=
    for a in "$@"; do
        case "$a" in -y|-Y|-yes|-Yes|-YES|--yes) YES=1 ;; esac
    done
    if [ -z "$YES" ]; then
        printf 'Proceed? (y/N) '
        read -r a
        case "$a" in y|Y|yes) ;; *) echo "aborted, nothing done."; exit 0 ;; esac
    fi
    echo "> git push -u origin $BR"
    git push -u origin "$BR" || fail "push rejected - origin/$BR has commits you don't have. git pull, then re-run ship."
    run git checkout main
    run git pull --ff-only origin main
    echo "> git merge --no-ff $BR"
    if ! git merge --no-ff "$BR" -m "Merge branch '$BR'"; then
        git merge --abort
        git checkout "$BR"
        fail "merge conflicts with main. On $BR run: git merge main (resolve, commit), then re-run ship."
    fi
    run git push origin main
    run git branch -d "$BR"
    run git push origin --delete "$BR"
    echo
    echo "DONE - $BR merged into main and deleted (local + origin)."
    ;;

*)
    echo "usage: scripts/shoddy-branch.sh feature NAME   create feature/NAME off up-to-date main"
    echo "       scripts/shoddy-branch.sh bug NAMENN     create bug/NAMENN off up-to-date main"
    echo "       scripts/shoddy-branch.sh ship [-y|-Yes]  push current feature/bug branch, merge --no-ff into main, delete it"
    exit 2
    ;;
esac
