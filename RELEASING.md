# Releasing Shoddy

How a version gets cut, in full, and why the procedure is as short as it is.
**If you want the sequence rather than the reasoning, that is
[WORKFLOW.md](WORKFLOW.md), and it is the page to follow.**

---

## The short version

```powershell
./build.ps1 test          # the C# suite, every core and machine suite, every mill
./build.ps1 check         # docs, errors, permissions, host-blind, suites, twins, lanes
scripts/shoddy-display.ps1       # the three windowed suites, if windowed code changed
# write release-notes/vX.Y.Z.md, commit it, merge the PR with CI green
./scripts/shoddy-ship.ps1 2.6.0  # tag and push - CI publishes
```

Every script has a `.sh` twin taking the same arguments, for Linux and macOS.
**On Windows run the `.ps1`** — it is the path that actually ships there, and
the twin that goes unexercised is the twin that rots. `verify-twins.js`
exists because that has happened: a release once had to fix "native stderr
fatal in every PowerShell script", a fault that sat through several releases
because nobody ran the PowerShell half.

## The tag is the version

This is the load-bearing decision, and everything short about the procedure
follows from it.

**Nothing in this repository records a release number.**
`Directory.Build.props` and `vscode-shoddy/package.json` carry development
defaults; a release takes its number from the git tag, and `release.yml`
stamps both files on the runner — uncommitted — before building.

**Everything a version-in-a-file needs, this does not need.** No bump
commit. No `release/VX.Y.Z` branch to cut. No merge back. No half-finished
sequence to recover from, because there is no sequence — pushing the tag is
the whole of it, and a step that does not exist cannot fail with the
previous ones already done. The old procedure was eight operations, each of
which could fail with the previous seven already done, and it carried a
documented recovery procedure for exactly that.

**The cost, stated plainly:** a locally built mill or sparky reports the
development default whatever branch it came from. That is honest — a local
build is not a release — and the version a user sees on a downloaded
archive is stamped from the tag that built it.

## There is no gate harness

There used to be one: a step registry, a receipt store keyed to the working
tree, `--resume`, and a selftest to prove the harness itself. It existed
because CI was path-filtered and partial, so nothing but a local run proved
the whole tree. A receipt store also has a failure mode worth avoiding: one
keyed on yesterday's tree will happily print a pass, in a tenth of a
second, describing work that no longer exists.

**What replaces it is CI's own verdict.** `ci.yml` carries no path filters
and proves everything on every push — the checks, the core suite, the MCP
lane, the MAUI lane on a Windows runner, and the headless container.
`scripts/shoddy-ship.ps1` asks GitHub whether the commit it is about to tag
passed, and refuses if it did not. That is a better answer than a local
receipt for the same reason a build artefact is better than a build log: it
is the machine that actually does the work, reporting on the actual work.

Locally, `./build.ps1 test` is still the whole suite — roughly twenty
minutes — and `./build.ps1 check` is the fast half, seconds. Run them
before opening the pull request; CI runs them again either way.

If `gh` is not installed the CI check is skipped with a warning rather than
failing — an unavailable tool should not block a release — but the workflow
re-runs the whole suite on the tagged commit anyway, so nothing ships
unproven.

## There is one way to ship

`scripts/shoddy-ship.ps1` has no primitive underneath it that skips the checks.
The previous arrangement had two entry points that both tagged and pushed,
one of which quietly skipped the proof, and the documentation needed a
comparison table warning you off the wrong one. A second door into a
release is a door somebody uses at 2am.

**Pushing the tag is the moment it ships.** `scripts/shoddy-ship.ps1` prints what
is about to happen and asks before doing it. Add `-Yes` from a
non-interactive shell — spelled the same way in both twins, deliberately,
because a pair that took `-Yes` in PowerShell and `-y` in the shell while
the docs called them identical is one of the two drifts `verify-twins.js`
was written for.

## No path filters on CI

`ci.yml` runs on every push and every pull request, with no `paths` or
`paths-ignore`.

This is deliberate and it is a reaction to a specific failure. Every lane
used to be path-filtered, so a change outside a lane's paths ran none of
its suites — and `Directory.Build.props`, which sets the version every
binary reports, matched no filter at all. Three of four test projects were
proved only by workflows a given push might never trigger.

The cost is a doc typo rebuilding every lane for nothing. That is a few
runner-minutes. When the choice is between wasting a runner and not
knowing, waste the runner.

## What the release asserts before it publishes

Each of these is a check because the thing it checks has gone wrong
somewhere:

**The whole suite, again, from the tag.** CI already proved this commit and
`ship` refuses to tag one CI has not passed — but a release is the one
thing that cannot be un-shipped, and this costs minutes.

**The package was produced at the tag's version.** vsce packages at
`package.json`'s version, which the stamping step set from the tag, so the
`.vsix` name and the tag can no longer disagree.

**The unix sparky archive carries the executable bit.** It only survives a
tar made on a filesystem that has one; an archive cut on Windows cannot
record it at all, and a binary that arrives without it does not run. This
is why the release job is a Linux runner, and it is load-bearing rather
than incidental.

## The suites CI cannot run

`scripts/shoddy-display.ps1` runs seedscribblertest, seedturtletest and
seedplottertest — the three suites that open a real window. A hosted runner
has no platform for GLFW to open one on, not even under Xvfb (v1.10.1
shipped a gate for it and it failed the same way). The list lives in
`TST_EXCLUSIONS` in `scripts/suites.mjs`, the same list `verify-suites.js`
grades every `tst/*.shoddy` against, so a fourth windowed suite cannot be
added there and quietly forgotten.

They are the one part of the proof that stays manual: run them on a machine
with a display whenever windowed code changes, before shipping. `ship`
prints a reminder.

## The executable bit, before it reaches CI

`verify-permissions.js` checks the bit on every tracked script **in git's
index**, not on the filesystem — a checkout with `core.filemode=false`, the
default on Windows, makes a raw `chmod` invisible to git no matter what the
filesystem did.

It has two modes on purpose. Locally it **stages the fix**, because
reporting a wrong bit and making the reader go and correct it by hand is
how four of them once shipped at once — one of which took down a release
with "Permission denied", exit 126. In CI it runs with `--check`, which
fixes nothing and fails instead: a runner's index is thrown away at the end
of the job, so a fixing run there would repair the bit, print a cheerful
line, exit 0, and let the fault sail through. **A gate that repairs the
evidence is not a gate.** The window between writing a `.sh` and adding it
is covered separately: `scripts/shoddy-commit.ps1` stages any new `.sh` with
`--chmod=+x`, because a check that walks tracked files cannot see a script
that has never been added.

## Release notes

**Where:** one file per release in [`release-notes/`](release-notes/),
named for its tag — `v2.6.0` → `release-notes/v2.6.0.md`. The whole file
becomes the body of the GitHub Release.

**When: before you tag, and committed.** The workflow checks out the tag
and reads only what that commit contains. `scripts/shoddy-ship.ps1` refuses to tag
without the file, so this is a re-run rather than a bad release — but if a
tag is pushed by hand without one, the workflow falls back to a list of
commit subjects. Never empty, and never worth linking to.

## After it ships

**Smoke the download.** Install the `.vsix` from the release page
(`code --install-extension vscode-shoddy-X.Y.Z.vsix`), open a `.shoddy`
file, run it. Unpack the sparky archive for your platform and point an MCP
client at it.

**Docs deploy themselves.** A merge touching `docs/**` triggers the Pages
workflow, which regenerates the sitemap (`scripts/shoddy-make-sitemap.sh`) and
publishes `docs/`. Verify in the Actions tab.

## If a release fails

**The tag is already public.** Fix forward with a new patch version. Never
move a tag someone may have fetched, and never delete one — a consumer who
fetched it has it, and a moved tag means two different builds answer to the
same number.

If `scripts/shoddy-ship.ps1` created the tag but the push failed, nothing has
shipped: the tag is local only, and `git tag -d vX.Y.Z` puts you back where
you started. The script says so when it happens.
