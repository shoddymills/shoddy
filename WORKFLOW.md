# Branch to tag, start to finish

The maintainer's path for one piece of work: cut a branch, build the thing,
prove it, ship it. Every command here is the Windows `.ps1`; the `.sh` twin
takes the same arguments.

This page is the sequence. The reference behind each step is
[RELEASING.md](RELEASING.md) — read that when something goes sideways, not
to find out what to type next.

| | | |
|---|---|---|
| 1 | **Branch** | `scripts/shoddy-feature.ps1 new NAME` — or a hand-cut `bug/NAME NN` for a fix |
| 2 | **Work** | edit, `./build.ps1 run FILE.shoddy`, repeat |
| 3 | **Prove** | `scripts/shoddy.ps1 preflight` then `gate --resume` |
| 4 | **Notes** | `release-notes/vX.Y.Z.md`, on the branch, before shipping |
| 5 | **Merge** | `scripts/shoddy-feature.ps1 ship` |
| 6 | **Release** | `scripts/shoddy-release.ps1 X.Y.Z` |
| 7 | **Watch** | Actions → Release |

---

## 1 — Branch

Names come from the curated list in `shoddy-planning/list-of-branch-names.md`
— Heavy Woollen District mill towns, one per branch, struck through once
used. Take the next unstruck one and strike it in the same edit.

```powershell
scripts/shoddy-feature.ps1 new denholme        # -> feature/denholme
```

It fetches, refuses a name already taken locally or on origin, cuts from an
up-to-date `main`, and switches you to it. A leading `feature/` is stripped,
so both spellings work.

**No work happens on `main`.** It receives `--no-ff` merges and nothing else.

### Fixing something already released

A fix is **not** a feature branch and does not consume a name from the
curated list. It is `bug/<origin-feature>NN`, numbered per origin feature:
`bug/cleckheaton03` is the third fix to work that shipped from
`feature/cleckheaton`.

**Cut it by hand.** `shoddy-feature.ps1 new` always builds `feature/$NAME`,
so `new bug/cleckheaton06` would quietly give you `feature/bug/cleckheaton06`.

```powershell
git log --oneline main --grep="Merge branch 'bug/"   # which numbers are taken
git checkout main
git pull --ff-only origin main
git checkout -b bug/cleckheaton06
```

From there it is identical: work, prove, `ship`. `shoddy-feature.ps1 ship`
runs from `feature/*` **and** `bug/*` and merges both the same way.

### Fixes accumulate; a patch release bundles them

One fix does not oblige one release. Several can sit merged on `main` waiting
for a patch — that is the normal state, not a backlog.

```powershell
git describe --tags --abbrev=0 main                  # the last release
git log --oneline "$(git describe --tags --abbrev=0 main)..main" --grep="Merge branch 'bug/"
```

Whatever that lists is what your next patch ships, and what its release notes
have to describe. **Write one notes file covering all of them** — the file is
named for the tag, not for a branch, so `release-notes/v2.0.3.md` covers every
fix merged since `v2.0.2`, however many branches they arrived on.

Then §6 as normal. Nothing about the release path differs for a patch.

## 2 — Work

Nothing here is ceremonial — it is just the fast loop.

```powershell
./build.ps1 run mills/halifax/halifax.shoddy   # compile in memory and run
./build.ps1 weave FILE.shoddy                  # compile only: catches every
                                               # invented word in a second
mills/halifax/build.ps1 test                   # one mill's own suite
./bin/mill.exe run tst/alg.shoddy              # one machine's suite
```

**Write the tests first**, from what the change must be true of, and never
edit a test to make code pass — if it fails, either the code is wrong or the
requirement was, and both are worth knowing. A suite is an ordinary program
full of `Assert(cond, "MESSAGE")`; a run that reaches the end passed.

`weave` is the cheapest check there is. Reach for it constantly; save the
gate for when you think you are done.

## 3 — Prove

Two commands, and the first is cheap enough to run whenever you like.

```powershell
scripts/shoddy.ps1 preflight        # ~3s, read-only, tolerates a dirty tree
scripts/shoddy.ps1 gate --resume    # the expensive proof
```

`preflight` checks the environment, the git state and all five `verify-*`
gates. It is read-only and mutates nothing, so there is no reason not to run
it mid-work.

`gate` is the whole proof: build, machines, the C# conformance suite, every
`tst/` and machine suite, and all thirteen mills. Roughly fifteen minutes
cold, and `--resume` skips whatever is already green **for the current
commit**, so a retry does not repeat the hour that already passed.

Every step carries a timeout, its own log, and a remedy on failure. A step
that hangs is killed with its whole process tree and named; nothing is judged
on anything but its exit code.

```powershell
scripts/shoddy.ps1 status                    # what is green for this commit
scripts/shoddy.ps1 gate --only mill-halifax  # re-run one step
scripts/shoddy.ps1 gate --from machine-jsontest
scripts/shoddy.ps1 selftest                  # prove the harness itself
```

**`scripts/shoddy.ps1 clean` is a deliberate act, not a habit.** It shuts the
build servers down and clears orphaned test hosts, which is what you want
when something is genuinely wedged — but it also cold-starts the MSBuild
cache, and several C# tests shell out to `dotnet build` and `dotnet run`. On
a cold cache those go quiet long enough for the blame collector to kill the
test host, and the suite reports a crash partway through. Use it when stuck,
not before every run.

## 4 — Release notes

**On the branch, before you ship, and committed.** The Release workflow
checks out the *tag* and reads only what that commit contains, so notes
written afterwards are invisible to it.

One file per release, named for its tag: `release-notes/v2.0.3.md`. The whole
file becomes the body of the GitHub Release; don't repeat the version as a
heading. Format and an example are in
[release-notes/README.md](release-notes/README.md).

Lead with what a user notices. Fixed bugs that bit someone, new machines and
builtins, language surface changes, breaking changes first and unmissable.

## 5 — Merge

```powershell
scripts/shoddy-feature.ps1 ship          # add -Yes from a non-interactive shell
```

Pushes the branch, merges it `--no-ff` into `main`, pushes `main`, and
deletes the branch locally and on origin. It prints the commits it is about
to merge and asks first. On a conflict it aborts cleanly and puts you back on
your branch.

Runs from `feature/*` or `bug/*` only, and refuses on a dirty tree or when
there is nothing to merge.

## 6 — Release

Confirm the version is coherent before touching anything that pushes:

```powershell
scripts/shoddy.ps1 preflight 2.0.3
```

With a version named, `preflight` turns strict: the tree must be clean, the
notes file must exist, and the tag must be free both locally and on origin.

```powershell
scripts/shoddy-release.ps1 2.0.3         # add -Yes to skip the prompt
```

That one command fast-forwards `main`, cuts `release/V2.0.3`, runs
`./build.ps1 all 2.0.3` (clean, test, package — a red test stops everything
with nothing pushed), commits the version bump, pushes the branch, **tags
`v2.0.3` and pushes the tag**, then merges `--no-ff` back into `main`.

**Pushing the tag is the moment it ships.** It refuses to start unless the
version is an exact `X.Y.Z`, you are at the repo root, the tree is clean, no
merge is in progress, and neither branch nor tag already exists.

**Shut the build servers down first** — `dotnet build-server shutdown`.
`build.ps1 all` begins by deleting `bin/`, and a leftover server holding
`bin/mill.exe` open kills the release *after* it has cut the release branch.

## 7 — Watch, then verify

Actions → **Release**. It verifies the tag matches `package.json`, rebuilds
from scratch, composes the body from `release-notes/vX.Y.Z.md`, and publishes
the GitHub Release with the `.vsix`, one sparky archive per OS and one
fettle archive per OS attached. It also records a deployment for each of
`sparky` and `fettler`, which is what puts them in the repo home's
Deployments sidebar beside Pages.

The unix archives are cut on the Linux runner deliberately: the execute
bit only survives a tar made on a filesystem that has one, and a `fettle`
that arrives without it does not run.

```powershell
git log --oneline --graph -6      # feature bubble + release bubble
git ls-remote --tags origin       # the tag is public
```

**If the workflow fails, the tag is already public.** Fix forward with a new
patch version. Never move a tag someone may have fetched.

Docs deploy themselves: a merge touching `docs/**` triggers the Pages
workflow. Nothing to do.

---

## When it goes wrong

| Symptom | First move |
|---|---|
| A gate step hangs or behaves oddly | `scripts/shoddy.ps1 clean`, then re-run with `--resume` |
| "Test host process crashed", count well short of the total | Not a failing test — something took the process down. See the step's log; the remedy names the two known causes |
| `git update-index --chmod=+x` says "cannot add to the index" | The file is untracked. `git add --chmod=+x <file>` does both |
| A release died mid-flight | The script prints its own undo. Usually `git checkout main; git branch -D release/VX.Y.Z` |
| Unsure whether the tree is releasable | `scripts/shoddy.ps1 status` |

## What is where

| | |
|---|---|
| This page | the sequence |
| [RELEASING.md](RELEASING.md) | the reference behind every release step, and the war stories |
| [CONTRIBUTING.md](CONTRIBUTING.md) | outside contributors: fork, PR, licence |
| [CLAUDE.md](CLAUDE.md) | repo rules for an assistant working here |
| `scripts/shoddy.ps1 help` | every gate command and flag |
