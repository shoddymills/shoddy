# Branch to tag, start to finish

The maintainer's path for one piece of work: cut a branch, build the thing,
prove it, ship it. Every command here is the Windows `.ps1`; the `.sh` twin
takes the same arguments, flags included.

**Every step is a script except one.** The pull request is a person reading a
diff and a green CI run and deciding, and nothing here automates that. The
scripts stop either side of it.

This page is the sequence. The reference behind each step (and the reasoning
that made it this short) is [RELEASING.md](RELEASING.md).

**Lost part-way through it?** `./scripts/shoddy-status.ps1 X.Y.Z` reports where the
work is (branch, commits, pushed, pull request, CI's verdict, notes,
tag) and names the one command that comes next. It is the only script here
that reports rather than acts, so it is always safe to run.

| | | |
|---|---|---|
| 1 | **Branch** | `./scripts/shoddy-branch.ps1 feature NAME`, or `bug NAMENN` for a fix |
| 2 | **Work** | edit, `./build.ps1 run FILE.shoddy`, `./scripts/shoddy-commit.ps1 -Message "..."` |
| 3 | **Notes** | `release-notes/vX.Y.Z.md`, on the branch, committed |
| 4 | **Prove** | `./build.ps1 prove`, on the last commit, tree clean |
| 5 | **Review** | `./scripts/shoddy-pr.ps1`; **then a person merges it on GitHub** |
| 6 | **Land** | `./scripts/shoddy-branch.ps1 land`, then wait for CI on main |
| 7 | **Ship** | `./scripts/shoddy-ship.ps1 X.Y.Z` - refuses until CI is green on main |
| 8 | **Watch** | Actions &rarr; Release |

---

## 1: Branch

Feature names come from the curated list in
`shoddy-planning/list-of-branch-names.md`: Heavy Woollen District mill
towns, one per branch, struck through once used. Take the next unstruck one
and strike it in the same edit.

```powershell
./scripts/shoddy-branch.ps1 feature denholme     # -> feature/denholme
```

It fetches, refuses a name already taken locally or on origin, cuts from an
up-to-date `main`, and switches you to it. A leading `feature/` is stripped,
so both spellings work.

**No work happens on `main`.** It receives merges and nothing else, and
`scripts/shoddy-commit.ps1` refuses there rather than trusting anyone to remember.

### Fixing something already released

A fix is **not** a feature branch and does not consume a name from the
curated list. It is `bug/<origin-feature>NN`, numbered per origin feature:
`bug/cleckheaton03` is the third fix to work that shipped from
`feature/cleckheaton`. `bug` refuses a name with no two-digit suffix.

```powershell
git --no-pager log --oneline main --grep="Merge"   # which numbers are taken
./scripts/shoddy-branch.ps1 bug cleckheaton06
```

From there it is identical: work, prove, `pr`, merge, `land`.

### Fixes accumulate; a patch release bundles them

One fix does not oblige one release. Several can sit merged on `main`
waiting for a patch; that is the normal state, not a backlog. Whatever has
merged since the last tag is what your next patch ships, and what its
release notes have to describe: **write one notes file covering all of
them**; the file is named for the tag, not for a branch.

## 2: Work

Nothing here is ceremonial; it is the fast loop.

```powershell
./build.ps1 run mills/halifax/halifax.shoddy   # compile in memory and run
./build.ps1 weave FILE.shoddy                  # compile only: catches every
                                               # invented word in a second
mills/halifax/build.ps1 test                   # one mill's own suite
./build.ps1 run tst/alg.shoddy                 # one machine's suite
./scripts/shoddy-commit.ps1 -Message "what changed"   # stage everything and commit
```

`scripts/shoddy-commit.ps1` **pushes nothing.** Committing and publishing are
separate decisions and it only makes the first. It also refuses on an empty
message, on a clean tree, and mid-merge, and it adds any new file that opens
with `#!` with `--chmod=+x`, because `verify-permissions` walks *tracked*
files and cannot see a script that has never been added.

**Write the tests first**, from what the change must be true of, and never
edit a test to make code pass; if it fails, either the code is wrong or the
requirement was, and both are worth knowing. A suite is an ordinary program
full of `Assert(cond, "MESSAGE")`; a run that reaches the end passed.

`weave` is the cheapest check there is. Reach for it constantly.

If `main` moves while you work:

```powershell
./scripts/shoddy-branch.ps1 sync     # merge main into your branch, deliberately, tree clean
```

## 3: Release notes

**On the branch, before you prove, and committed.** The Release workflow
checks out the *tag* and reads only what that commit contains, so notes
written afterwards are invisible to it; and `prove` proves a commit, so the
notes belong in it.

One file per release, named for its tag: `release-notes/v2.6.0.md`. The
whole file becomes the body of the GitHub Release; do not repeat the version
as a heading. Format and an example are in
[release-notes/README.md](release-notes/README.md).

Lead with what a user notices. Fixed bugs that bit someone, new machines and
builtins, language surface changes, breaking changes first and unmissable.

## 4: Prove

```powershell
./build.ps1 prove    # everything CI and the release run that this machine can
```

**One command, and it is the same proof CI gives.** In CI's order: `check`
(the seven fast gates, read-only), `test` (the C# suite, every core and
machine suite, every mill, the manifests, the headless refusal),
`reproducible` (two clean builds agree byte for byte), the MCP lane's
`build`, `test`, `release` and a versioned `publish` - each verb grading
its own output, the last one checking every unix archive's execute bit and
driving this platform's binary - then `vsix` at a throwaway version, the
packaging the Release workflow does; on Windows the MAUI lane; and the
three display suites. It runs in **CI mode** (`SHODDY_CI=1`): locked
restore and CI build settings, exactly as on the runner, so a drifted lock
file or a non-reproducible build fails here rather than after a push. It
needs vsce, and says so first if it is missing.

ci.yml's checks are those same verbs and gates; its other steps only
provision the runner. What this machine cannot run is printed at the end
as `NOT PROVED ON THIS MACHINE` - off Windows the MAUI lane, and everywhere
the bare headless container - and CI proves those. Nothing is skipped in
silence.

**It proves a commit.** Run it on the last commit with a clean tree, and it
records `artifacts/proved/<commit>`; `shoddy-pr` then says in the pull
request that `prove` passed on that commit, or says that it did not run. A
run that starts dirty records nothing. A run that ends dirty fails: some
step wrote into the tree.

**The display suites** (seedscribbler, seedturtle, seedplotter) open a real
window, and a hosted runner has no platform to open one on, not even under
Xvfb. `prove` runs them wherever there is a display; on their own they are
`scripts/shoddy-display.ps1`. The list lives in `TST_EXCLUSIONS` in
`scripts/suites.mjs`, so a fourth windowed suite cannot be added there and
forgotten here.

`test` and `check` are still there for the fast loop while you work.
`prove` is the one that says the work is ready.

## 5: Review

```powershell
./scripts/shoddy-pr.ps1              # push the branch and open the pull request
```

It pushes, opens the pull request (or pushes to the one already OPEN), and
stops. The body states whether `prove` passed on the commit under review.
**The merge is a person** reading the diff and a green CI run and pressing
the button on GitHub; nothing in this repository automates that, and
`land` refuses on a branch whose pull request is not merged, so there is no
back way round it.

## 6: Land

```powershell
./scripts/shoddy-branch.ps1 land     # after the merge: back to main, branch deleted
```

Returns to an up-to-date `main` and deletes the branch locally and on
origin. It asks GitHub whether the pull request has merged before
deleting anything.

**Then wait for CI on main.** The merge is a new commit, ci.yml runs on it,
and `ship` refuses until that run has finished green. `./scripts/shoddy-status.ps1
X.Y.Z` says when: its `next` line names `shoddy-ship` only then.

## 7: Ship

```powershell
./scripts/shoddy-ship.ps1 2.6.0      # tag v2.6.0 on main and push it - CI publishes
```

**The tag is the version.** No file records it: `release.yml` reads the
number from the tag and passes it to the build verbs - `build.sh all X.Y.Z`
packages the extension at it, `hosts/mcp/build.sh publish X.Y.Z` publishes
sparky at it - the same verbs CI and `prove` run at a throwaway version.
So there is no bump commit, no `release/V*` branch, and nothing to merge
back; pushing the tag is the whole of it, and the moment it ships.

`ship` refuses: off `main`, a dirty tree, main ahead of or behind origin, a
taken tag, missing or uncommitted notes, and any commit whose ci.yml run is
missing, still running, or anything but a success. It needs `gh` to ask,
and refuses without it. Add `-Yes` from a non-interactive shell.

## 8: Watch, then verify

Actions &rarr; **Release**. It re-runs the whole suite from the tag, packages the
`.vsix` at the tag's version, publishes one sparky archive per OS (checking
every unix archive's execute bit and the version the binary reports),
composes the body from `release-notes/vX.Y.Z.md`, publishes the GitHub
Release, and records the sparky deployment.

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
| A verify gate is red | `./build.ps1 check` locally; each gate prints the file and the fix. A missing execute bit: `node scripts/verify-permissions.js` stages it |
| CI red on the pull request | the failing job's log names the suite; run the same command locally |
| "Test host process crashed", count well short of the total | Not a failing test; something took the process down. Two known causes, both fixed and both easy to reintroduce: a pipe deadlock from reading one child stream to the end before the other (ProcessRun.cs), and parallel tests sharing process-global state (AssemblyInfo.cs) |
| A new script fails CI with "Permission denied", exit 126 | it was committed without its execute bit; `scripts/shoddy-commit.ps1` stages every new file that opens with `#!` with `--chmod=+x`, and by hand it is `git add --chmod=+x <file>` |
| `prove` ends "NOT RECORDED: the run left changes in the tree" | a step wrote into the source tree; the listing names the file - make that step write under `artifacts/` or a temp folder |
| A release died mid-flight | the tag is already public: fix forward with a new patch version |
| Unsure what is proven, or where you are | `./scripts/shoddy-status.ps1 X.Y.Z` |

## What is where

| | |
|---|---|
| This page | the sequence |
| [RELEASING.md](RELEASING.md) | the reasoning behind every release step |
| [CONTRIBUTING.md](CONTRIBUTING.md) | outside contributors: fork, PR, licence |
| [CLAUDE.md](CLAUDE.md) | repo rules for an assistant working here |
| `scripts/` | status, branch, commit, pr, ship, display, make-sitemap, and the verify-*.js gates |
| `./build.ps1 help` | every build verb |
