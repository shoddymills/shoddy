#!/usr/bin/env bash
# MAINTAINER TOOL - pushes to origin/main and deletes remote branches. Assumes write access.
# Run from the repo root.
#
# scripts/shoddy-feature.sh new NAME    create feature/NAME off up-to-date main and switch to it
# scripts/shoddy-feature.sh ship [-y]   push the current feature branch, merge --no-ff into main,
#                                       push main, then delete the branch (local + origin)
#
# Guards: clean tree required; 'new' refuses names already taken (local or origin);
# 'ship' only runs from a feature/* branch, refuses when there is nothing to merge,
# and on merge conflict aborts cleanly and puts you back on your branch.
# A leading 'feature/' on NAME is stripped, so both spellings work.
set -u

fail() { echo "STOPPED: $*" >&2; exit 1; }
run()  { echo "> $*"; "$@" || fail "'$*' failed."; }

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "not inside a git repository."
git diff --quiet || fail "unstaged changes present - commit or stash first (see git status)."
git diff --cached --quiet || fail "staged-but-uncommitted changes present - commit or unstage first."
git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 && fail "a merge is in progress - finish or abort it first."

case "${1:-}" in

new)
    NAME="${2:-}"
    [ -n "$NAME" ] || fail "usage: scripts/shoddy-feature.sh new NAME"
    NAME="${NAME#feature/}"
    echo "$NAME" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]*$' || fail "branch name may use letters, digits, . _ - only (got '$NAME')."
    BR="feature/$NAME"
    run git fetch origin --prune
    git show-ref --verify --quiet "refs/heads/$BR" && fail "$BR already exists locally."
    git show-ref --verify --quiet "refs/remotes/origin/$BR" && fail "$BR already exists on origin."
    run git checkout main
    run git pull --ff-only origin main
    run git checkout -b "$BR"
    echo
    echo "On $BR (cut from up-to-date main)."
    echo "Work, commit, then: scripts/shoddy-feature.sh ship"
    ;;

ship)
    BR="$(git rev-parse --abbrev-ref HEAD)"
    case "$BR" in
        feature/*) ;;
        *) fail "current branch is '$BR' - ship only runs from a feature/* branch." ;;
    esac
    run git fetch origin --prune
    N="$(git rev-list --count origin/main..HEAD)"
    [ "$N" -gt 0 ] || fail "no commits on $BR beyond origin/main - nothing to merge."
    echo
    echo "Will merge these $N commit(s) from $BR into main, then delete the branch:"
    git log --oneline origin/main..HEAD
    if [ "${2:-}" != "-y" ]; then
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
    echo "usage: scripts/shoddy-feature.sh new NAME   create feature/NAME off up-to-date main"
    echo "       scripts/shoddy-feature.sh ship [-y]  push current feature branch, merge --no-ff into main, delete it"
    exit 2
    ;;
esac
