#!/usr/bin/env bash
# MAINTAINER TOOL - pushes to origin/main and creates tags. Assumes write access.
#
# scripts/shoddy-release.sh X.Y.Z [-y]   (run from the repo root)
#
# The whole release, from a clean repo, in one shot:
#   main fast-forwarded to origin/main -> release/VX.Y.Z cut -> ./build.sh all X.Y.Z
#   (clean + test + package; a red test stops everything) -> commit package.json ->
#   push branch -> tag vX.Y.Z -> push tag -> merge --no-ff into main -> push.
#
# Pushing the tag is what ships: the Release workflow rebuilds on a clean runner
# and publishes the GitHub Release with the .vsix attached. The local build here
# is the gate, not the artifact - nothing is pushed until it is green. The .vsix
# is build output and is never committed.
#
# Refuses to start unless every precondition holds: exact X.Y.Z version, repo root,
# clean tree, no merge in progress, branch/tag not already taken (local or origin).
# -y skips the confirmation prompt.
set -u

fail() { echo "RELEASE STOPPED: $*" >&2; exit 1; }
run()  { echo "> $*"; "$@" || fail "'$*' failed."; }

# --- argument ---
VER="${1:-}"
[ -n "$VER" ] || fail "usage: scripts/shoddy-release.sh X.Y.Z [-y]   (e.g. scripts/shoddy-release.sh 1.0.0)"
echo "$VER" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || fail "version must be exactly X.Y.Z, digits only (got '$VER')."
BR="release/V$VER"
TAG="v$VER"
VSIX="vscode-shoddy/vscode-shoddy-$VER.vsix"

# --- preconditions: right place, clean state ---
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "not inside a git repository."
[ "$(git rev-parse --show-toplevel)" = "$(pwd -P)" ] || fail "run from the repo root: $(git rev-parse --show-toplevel)"
{ [ -f build.sh ] && [ -f vscode-shoddy/package.json ]; } || fail "this doesn't look like the shoddy repo root."
git diff --quiet || fail "unstaged changes present - commit or stash first (see git status)."
git diff --cached --quiet || fail "staged-but-uncommitted changes present - commit or unstage first."
git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 && fail "a merge is in progress - finish or abort it first."

# --- preconditions: name not taken anywhere ---
run git fetch origin --tags --prune
git show-ref --verify --quiet "refs/heads/$BR" && fail "branch $BR already exists locally."
git show-ref --verify --quiet "refs/remotes/origin/$BR" && fail "branch $BR already exists on origin."
git show-ref --verify --quiet "refs/tags/$TAG" && fail "tag $TAG already exists locally."
git ls-remote --exit-code --tags origin "$TAG" >/dev/null 2>&1 && fail "tag $TAG already exists on origin."

# --- confirm ---
echo
echo "Release plan for $VER:"
echo "  main          -> fast-forwarded to origin/main"
echo "  $BR -> created; ./build.sh all $VER (clean + test + package)"
echo "  commit + push -> package.json ('$TAG release')"
echo "  tag + push    -> $TAG   (this is what triggers the Release workflow)"
echo "  main          -> merge --no-ff $BR, pushed"
echo
if [ -f "release-notes/$TAG.md" ]; then
    echo "  release notes -> release-notes/$TAG.md ($(wc -l < "release-notes/$TAG.md") lines)"
else
    echo "  release notes -> MISSING: release-notes/$TAG.md"
    echo "                   The release body will fall back to the merge log."
    echo "                   Notes must be committed BEFORE the tag: answer n,"
    echo "                   write them, commit, then re-run. See release-notes/README.md."
fi
if [ "${2:-}" != "-y" ]; then
    printf 'Proceed? (y/N) '
    read -r a
    case "$a" in y|Y|yes) ;; *) echo "aborted, nothing done."; exit 0 ;; esac
fi

# --- full checkout: release always builds from up-to-date main ---
run git checkout main
run git pull --ff-only origin main
run git checkout -b "$BR"

# --- clear stale packages so the version check below can't pass on an old file ---
rm -f vscode-shoddy/*.vsix

# --- build + test + package; nothing has been pushed yet ---
if ! ./build.sh all "$VER"; then
    echo "BUILD/TEST FAILED - nothing was pushed." >&2
    echo "inspect, then undo with: git checkout main && git branch -D $BR" >&2
    exit 1
fi
[ -f "$VSIX" ] || fail "expected package $VSIX was not produced."
grep -q "\"version\": \"$VER\"" vscode-shoddy/package.json || fail "package.json was not bumped to $VER."

# --- publish: the version bump is the only artifact that belongs in the commit ---
run git add vscode-shoddy/package.json
run git commit -m "$TAG release"
run git push -u origin "$BR"
run git tag "$TAG"
run git push origin "$TAG"
run git checkout main
run git merge --no-ff "$BR" -m "Merge branch '$BR'"
run git push origin main

echo
echo "DONE - $TAG is tagged and merged."
echo "The Release workflow is now building $TAG and will publish the GitHub Release"
echo "with vscode-shoddy-$VER.vsix attached. Watch it in the Actions tab; if it fails,"
echo "the tag is already public, so fix forward with a new patch version."
echo "Release branch kept as hotfix base: $BR"
echo "  (delete anytime: git branch -d $BR && git push origin --delete $BR)"
