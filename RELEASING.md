# Releasing Shoddy

How a version gets cut. Day-to-day contribution is in
[CONTRIBUTING.md](CONTRIBUTING.md); this is the maintainer's procedure.

**Branch discipline.** No work is ever committed directly on `main` — it only
receives `--no-ff` merge commits, pushed from the CLI. All release mechanics
happen on `release/VN.N.N`. Tags are `vN.N.N`.

**What ships is the tag.** Pushing `vN.N.N` triggers the
[Release workflow](.github/workflows/release.yml), which rebuilds from scratch on a
clean runner and publishes the GitHub Release with the packaged extension attached.
The `.vsix` is build output and is **never committed** — the release asset is the
only copy that ships.

---

## The short version

```sh
$EDITOR release-notes/v1.0.0.md         # write the notes FIRST — see below
scripts/shoddy-feature.sh ship          # merge your feature into main
scripts/shoddy-release.sh 1.0.0         # build, test, tag, push — CI publishes
```

Both scripts have `.ps1` and `.cmd` twins with identical behaviour; run them from the
repo root. Everything below is what those scripts do and why, for when something goes
sideways.

---

## Release notes — when and where

**Where:** one file per release in [`release-notes/`](release-notes/), named for its
tag — `v1.0.0` → `release-notes/v1.0.0.md`. The whole file becomes the body of the
GitHub Release. Don't repeat the version as a heading; the release is already titled
with it. Format guidance and an example are in
[release-notes/README.md](release-notes/README.md).

**When: before you cut the release, and committed.** This is the part that bites. The
Release workflow checks out the *tag* and reads only what that commit contains, so
notes written after tagging are invisible to it. Concretely, the notes file has to be
committed and merged into `main` before you run `shoddy-release.sh` — easiest is to
write it on the same feature branch as the work it describes and let step 1 carry it
in.

**If you forget**, nothing breaks. `shoddy-release.sh` prints whether it found the
file in its confirmation block, *before* asking you to proceed:

```
  release notes -> MISSING: release-notes/v1.0.0.md
                   The release body will fall back to the merge log.
```

Answer `n`, write the notes, commit, re-run. If you go ahead anyway, the workflow
generates a body from the `--no-ff` merge subjects since the previous tag — never
empty, but it reads like a list of branch names. Acceptable for a patch nobody will
read; not for a release you're linking to. You can always edit the body on the
Releases page afterwards.

---

## 1 — Merge the feature into main

```sh
scripts/shoddy-feature.sh ship
```

Pushes the current `feature/*` branch, merges it `--no-ff` into `main`, pushes
`main`, and deletes the branch locally and on origin. It refuses to run with a dirty
tree, from a non-feature branch, or when there is nothing to merge, and on a merge
conflict it aborts cleanly and puts you back on your branch.

By hand, if you prefer:

```sh
git checkout main && git pull origin main
git merge --no-ff feature/x
git push origin main
```

`--no-ff` keeps the feature visible as a bubble in history.

## 2 — Cut the release

```sh
scripts/shoddy-release.sh 1.0.0
```

That single command does all of the following, and refuses to start unless the
version is an exact `X.Y.Z`, you are at the repo root, the tree is clean, no merge
is in progress, and neither the branch nor the tag already exists locally or on
origin:

1. Fast-forwards `main` to `origin/main` and cuts `release/V1.0.0`.
2. Runs `./build.sh all 1.0.0` — clean, then test, then package. **A red test stops
   everything, and nothing has been pushed at this point.** `all` is the only correct
   path for a release: `vsix` alone reuses a stale mill and runs no tests.
3. Commits the `vscode-shoddy/package.json` version bump as `v1.0.0 release`, and
   pushes the release branch.
4. Tags `v1.0.0` and pushes the tag — **this is the moment the release ships.**
5. Merges the release branch `--no-ff` into `main` and pushes.

The local build in step 2 is a gate, not a source of artifacts. The package that
reaches users is the one the Release workflow builds from the tag.

## 3 — Watch the Release workflow

Actions → **Release**. It verifies the tag matches `package.json`, runs
`./build.sh all`, composes the body from `release-notes/v1.0.0.md` (falling back to
the merge log if that file isn't in the tagged commit), and publishes the GitHub
Release with `vscode-shoddy-1.0.0.vsix` attached — one asset, batteries included: the
package carries its own mill and machines, so installing it needs only the .NET 10
runtime. The job log prints the body it chose under `--- release body ---`, so you can
see which path it took without opening the release.

If the workflow fails, the tag is already public. Fix forward with a new patch
version rather than deleting and re-pushing the tag — a moved tag breaks anyone who
already fetched it. To retry an unchanged tag after fixing the workflow itself, use
its **Run workflow** button and pass the tag.

## 4 — Smoke-test the published package

```sh
code --install-extension vscode-shoddy-1.0.0.vsix
```

Open a `.shoddy` file; Ctrl+R runs it via the bundled mill.

## 5 — Website

Nothing to do. Merges that touch `docs/**` trigger the
[Deploy docs to Pages](.github/workflows/pages.yml) workflow, which regenerates the
sitemap and publishes `docs/`. No docs changes, no deploy. Verify in the Actions tab.

## 6 — Clean up

`shoddy-feature.sh ship` already deleted the feature branch. The release branch is
kept as a hotfix base; the tag alone preserves the release point, so delete it
whenever you like:

```sh
git branch -d release/V1.0.0
git push origin --delete release/V1.0.0
```

## 7 — Verify

```sh
git log --oneline --graph -6      # feature bubble + release bubble + tag
git ls-remote --tags origin       # v1.0.0 present
```

The release page should show exactly one `.vsix` asset. Download and install it once
if you're feeling paranoid — it's the artifact users get, and nothing local proves it.

---

## If you have to do it by hand

The scripts are a convenience, not a dependency. The equivalent, in full:

```sh
# notes first — they must be in the commit the tag points at
git add release-notes/v1.0.0.md && git commit -m "notes for v1.0.0"

git checkout main && git pull origin main
git merge --no-ff feature/x && git push origin main

git checkout -b release/V1.0.0
./build.sh all 1.0.0                              # clean → test → machines → stage → vsix
git add vscode-shoddy/package.json                # the .vsix is gitignored
git commit -m "v1.0.0 release"
git push -u origin release/V1.0.0
git tag v1.0.0 && git push origin v1.0.0          # ← the Release workflow starts here

git checkout main
git merge --no-ff release/V1.0.0 && git push origin main
```

Unix `./build.sh`, Windows `build.cmd` or `./build.ps1` — same commands.
