# Branch to tag, start to finish

The maintainer's path for one piece of work: cut a branch, build the thing,
prove it, ship it. Every command here is the Windows `.ps1`; the `.sh` twin
takes the same arguments, flags included.

**Every step is a script except one.** The pull request is a person reading a
diff and a green CI run and deciding, and nothing here automates that. The
scripts stop either side of it.

This page is the sequence. The reference behind each step — and the reasoning
that made it this short — is [RELEASING.md](RELEASING.md).

| | | |
|---|---|---|
| 1 | **Branch** | `./scripts/branch.ps1 feature NAME` — or `bug NAMENN` for a fix |
| 2 | **Work** | edit, `./build.ps1 run FILE.shoddy`, `./scripts/commit.ps1 -Message "..."` |
| 3 | **Prove** | `./build.ps1 test` then `./build.ps1 check` |
| 4 | **Notes** | `release-notes/vX.Y.Z.md`, on the branch, committed |
| 5 | **Review** | `./scripts/pr.ps1` — **then a person merges it on GitHub** |
| 6 | **Land** | `./scripts/branch.ps1 land` |
| 7 | **Ship** | `./scripts/ship.ps1 X.Y.Z` |
| 8 | **Watch** | Actions → Release |

---

## 1 — Branch

Feature names come from the curated list in
`shoddy-planning/list-of-branch-names.md` — Heavy Woollen District mill
towns, one per branch, struck through once used. Take the next unstruck one
and strike it in the same edit.

```powershell
./scripts/branch.ps1 feature denholme     # -> feature/denholme
```

It fetches, refuses a name already taken locally or on origin, cuts from an
up-to-date `main`, and switches you to it. A leading `feature/` is stripped,
so both spellings work.

**No work happens on `main`.** It receives merges and nothing else — and
`scripts/commit.ps1` refuses there rather than trusting anyone to remember.

### Fixing something already released

A fix is **not** a feature branch and does not consume a name from the
curated list. It is `bug/<origin-feature>NN`, numbered per origin feature:
`bug/cleckheaton03` is the third fix to work that shipped from
`feature/cleckheaton`. `bug` refuses a name with no two-digit suffix.

```powershell
git --no-pager log --oneline main --grep="Merge"   # which numbers are taken
./scripts/branch.ps1 bug cleckheaton06
```

From there it is identical: work, prove, `pr`, merge, `land`.

### Fixes accumulate; a patch release bundles them

One fix does not oblige one release. Several can sit merged on `main`
waiting for a patch — that is the normal state, not a backlog. Whatever has
merged since the last tag is what your next patch ships, and what its
release notes have to describe: **write one notes file covering all of
them** — the file is named for the tag, not for a branch.

## 2 — Work

Nothing here is ceremonial — it is just the fast loop.

```powershell
./build.ps1 run mills/halifax/halifax.shoddy   # compile in memory and run
./build.ps1 weave FILE.shoddy                  # compile only: catches every
                                               # invented word in a second
mills/halifax/build.ps1 test                   # one mill's own suite
./bin/mill.exe run tst/alg.shoddy              # one machine's suite
./scripts/commit.ps1 -Message "what changed"   # stage everything and commit
```

`scripts/commit.ps1` **pushes nothing.** Committing and publishing are
separate decisions and it only makes the first. It also refuses on an empty
message, on a clean tree, and mid-merge — and it adds any new `.sh` with
`--chmod=+x`, because `verify-permissions` walks *tracked* files and cannot
see a script that has never been added.

**Write the tests first**, from what the change must be true of, and never
edit a test to make code pass — if it fails, either the code is wrong or the
requirement was, and both are worth knowing. A suite is an ordinary program
full of `Assert(cond, "MESSAGE")`; a run that reaches the end passed.

`weave` is the cheapest check there is. Reach for it constantly.

If `main` moves while you work:

```powershell
./scripts/branch.ps1 sync     # merge main into your branch, deliberately, tree clean
```

## 3 — Prove

```powershell
./build.ps1 test     # the C# suite, every core and machine suite, every mill
./build.ps1 check    # docs, errors, permissions, host-blind, suites, twins, lanes
```

`test` is the whole local proof — the same command CI's core job runs.
`check` is the fast half: seven read-only Node gates, seconds each. CI runs
both again on every push, plus the lanes a workstation may not have — the
MCP lane, the MAUI lane on a Windows runner, and the headless container —
so the pull request carries the full verdict either way.

**Three suites CI cannot run, and one command for them:**

```powershell
scripts/display.ps1          # seedscribbler, seedturtle, seedplotter
```

They open a real window; a hosted runner has no platform to open one on,
not even under Xvfb. The list lives in `TST_EXCLUSIONS` in
`scripts/suites.mjs`, so a fourth windowed suite cannot be added there and
forgotten here. Run it on a machine with a display whenever windowed code
changes; it refuses anywhere else rather than failing somewhere inside GLFW.

## 4 — Release notes

**On the branch, before you ship, and committed.** The Release workflow
checks out the *tag* and reads only what that commit contains, so notes
written afterwards are invisible to it.

One file per release, named for its tag: `release-notes/v2.6.0.md`. The
whole file becomes the body of the GitHub Release; don't repeat the version
as a heading. Format and an example are in
[release-notes/README.md](release-notes/README.md).

Lead with what a user notices. Fixed bugs that bit someone, new machines and
builtins, language surface changes, breaking changes first and unmissable.

## 5 — Review

```powershell
./scripts/pr.ps1              # push the branch and open the pull request
```

It pushes, opens (or updates) the pull request, and stops. **The merge is a
person** reading the diff and a green CI run and pressing the button on
GitHub — nothing in this repository automates that, and `land` refuses on a
branch whose pull request is not merged, so there is no back way round it.

## 6 — Land

```powershell
./scripts/branch.ps1 land     # after the merge: back to main, branch deleted
```

Returns to an up-to-date `main` and deletes the branch locally and on
origin. It asks GitHub whether the pull request really merged before
deleting anything.

## 7 — Ship

```powershell
./scripts/ship.ps1 2.6.0      # tag v2.6.0 on main and push it - CI publishes
```

**The tag is the version.** No file records it: `release.yml` reads the
number from the tag and stamps `Directory.Build.props` and
`vscode-shoddy/package.json` on the runner. So there is no bump commit, no
`release/V*` branch, and nothing to merge back — pushing the tag is the
whole of it, and the moment it ships.

`ship` refuses: off `main`, a dirty tree, main ahead of or behind origin, a
taken tag, missing or uncommitted notes, and a commit CI has not proven (it
asks GitHub). Add `-Yes` from a non-interactive shell.

## 8 — Watch, then verify

Actions → **Release**. It re-runs the whole suite from the tag, packages the
`.vsix`, publishes one sparky archive per OS, asserts the unix archive keeps
its execute bit, composes the body from `release-notes/vX.Y.Z.md`, publishes
the GitHub Release, and records the sparky deployment.

```powershell
git ls-remote --tags origin                         # the tag is public
code --install-extension vscode-shoddy-X.Y.Z.vsix   # smoke the download
```

**If the workflow fails, the tag is already public.** Fix forward with a new
patch version. Never move a tag someone may have fetched.

Docs deploy themselves: a merge touching `docs/**` triggers the Pages
workflow. Nothing to do.

---

## When it goes wrong

| Symptom | First move |
|---|---|
| A verify gate is red | `./build.ps1 check` locally; each gate prints the file and the fix |
| CI red on the pull request | the failing job's log names the suite; run the same command locally |
| "Test host process crashed", count well short of the total | Not a failing test — something took the process down. Two known causes, both fixed and both easy to reintroduce: a pipe deadlock from reading one child stream to the end before the other (ProcessRun.cs), and parallel tests sharing process-global state (AssemblyInfo.cs) |
| A new `.sh` fails CI with "Permission denied", exit 126 | it was committed without its execute bit; `scripts/commit.ps1` stages new `.sh` files with `--chmod=+x`, and by hand it is `git add --chmod=+x <file>` |
| A release died mid-flight | the tag is already public: fix forward with a new patch version |
| Unsure what is proven | `gh run list --commit $(git rev-parse HEAD)` |

## What is where

| | |
|---|---|
| This page | the sequence |
| [RELEASING.md](RELEASING.md) | the reasoning behind every release step |
| [CONTRIBUTING.md](CONTRIBUTING.md) | outside contributors: fork, PR, licence |
| [CLAUDE.md](CLAUDE.md) | repo rules for an assistant working here |
| `scripts/` | branch, commit, pr, ship, display, make-sitemap — and the verify-*.js gates |
| `./build.ps1 help` | every build verb |
