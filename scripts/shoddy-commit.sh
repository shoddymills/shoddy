#!/usr/bin/env bash
# MAINTAINER TOOL - stage everything and commit it. Writes history; pushes nothing.
# Run from anywhere inside the repo.
#
#   scripts/shoddy-commit.sh -Message "what changed"
#   scripts/shoddy-commit.sh -m "what changed"
#   scripts/shoddy-commit.sh -m "..." --force        allow it on main
#
# The twin of shoddy-commit.ps1 and equivalent to it. -Message is spelt the
# PowerShell way as well as the short way, because the twins in this folder
# once drifted over exactly that (-Yes against -y) while the documentation
# went on calling them interchangeable.
#
# IT REFUSES ON main, and that is the point of it. main receives merges and
# nothing else; work happens on a branch. A rule only a person can remember
# gets broken, so it is checked here, and --force is the deliberate exception
# that has to be typed out.
set -eu

fail() { echo "STOPPED: $*" >&2; exit 1; }
run()  { echo "> $*"; "$@" || fail "'$*' failed."; }

MESSAGE=
FORCE=

while [ $# -gt 0 ]; do
    case "$1" in
        -m|-Message|--message)
            [ $# -ge 2 ] || fail "$1 needs a message after it."
            MESSAGE="$2"; shift 2 ;;
        -f|-Force|--force)
            FORCE=1; shift ;;
        *)
            fail "unknown argument '$1' - usage: scripts/shoddy-commit.sh -m \"what changed\" [--force]" ;;
    esac
done

[ -n "${MESSAGE// /}" ] || fail 'the message is empty; say what changed.'

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail 'not inside a git repository.'

BRANCH="$(git rev-parse --abbrev-ref HEAD)" || fail 'could not read the current branch.'

if { [ "$BRANCH" = "main" ] || [ "$BRANCH" = "master" ]; } && [ -z "$FORCE" ]; then
    fail "on $BRANCH, which takes merges and not commits. Cut a branch, or pass --force if you mean it."
fi

git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 \
    && fail 'a merge is in progress - finish or abort it first.'

# Nothing to commit is not a failure worth a stack trace, but it must not be
# silence either: a task that reports success having done nothing is how you
# come to believe work is saved when it is not.
[ -n "$(git status --porcelain)" ] || fail 'nothing to commit - the tree is clean.'

# A .sh git has never seen is staged 100644 unless it is added with the bit
# set, and verify-permissions.js cannot cover it: that check walks TRACKED
# files, so the window between writing a script and adding it is invisible
# to it. v1.8.0 shipped four scripts through that window and one of them
# took the Release workflow down with "Permission denied", exit 126.
#
# `git update-index --chmod=+x` is the wrong tool here - it refuses a path
# git does not yet have, with a message about a missing --add option that
# reads like an unrelated fault. `git add --chmod=+x` does both at once.
#
# grep finding nothing exits 1, which under `set -e` would end the run, so
# it is allowed to fail. The loop is fed by IFS rather than a pipe: a pipe
# would put run/fail in a subshell, where a failure exits the subshell and
# the commit carries on regardless.
FRESH="$(git ls-files --others --exclude-standard | grep '\.sh$' || true)"
if [ -n "$FRESH" ]; then
    OLD_IFS=$IFS
    IFS='
'
    for f in $FRESH; do run git add --chmod=+x -- "$f"; done
    IFS=$OLD_IFS
fi

run git add -A
run git commit -m "$MESSAGE"

echo
echo "Committed on $BRANCH. Nothing has been pushed."
