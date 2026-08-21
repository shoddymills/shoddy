# Working in this repo

## Git rules always apply

The user's standing git rules (no work on `main`, only the user names branches,
never commit/push/tag without an explicit instruction in the current message)
are not project-specific — they apply here exactly as elsewhere, with no
exceptions carved out by anything below. Release work touching this repo is
the highest-stakes case, not a special case: `shoddy-release.sh` pushes
branches, tags, and merges to `main` in one run, so treat running it as
requiring the same explicit per-message authorization as any other
commit/push/tag, never as implied by being asked to "do the release" in
general terms or by a task reaching a point that feels ready to ship.

## Branch names

Branch names are drawn from the curated list in
`shoddy-planning\list-of-branch-names.md` (a sibling repo) — Heavy Woollen
District mill towns and villages, one per branch, struck through once used.
Picking the next unstruck name from that list is selecting from options the
user has already authored and approved, not inventing one; it satisfies
"only the user names branches" rather than being an exception to it.

When a name is chosen for a branch, you are authorized to mark it used in
that file, following the existing pattern — strike it through and note the
branch and reason, e.g.:

```
~~**Mirfield**~~ - Heavy woolen district participant. — **USED** (feature/mirfield, ...)
```

That authorization covers only this one edit (maintaining the list) and
does not extend to committing or pushing it, or to any other change in
`shoddy-planning` — those still need their own explicit go-ahead.

Exception to the no-work-on-`main` rule, scoped narrowly: `shoddy-planning`
is always on `main` and stays there — editing `list-of-branch-names.md`
directly on it is explicitly authorized and safe. This carve-out is for
that one file in that one repo only; it does not loosen the rule anywhere
else, including the rest of `shoddy-planning`.

## Release process is documented, not duplicated here

This project has a full maintainer release procedure — branch discipline,
pre-release doc/error verification, release-notes timing, the
`shoddy-branch.sh` / `shoddy-release.sh` scripts, and the GitHub Actions that
publish a release. Don't re-derive or restate it from memory or by guessing at
script behavior: read it fresh each time from source, since scripts and
workflows can change underneath a stale summary.

- [WORKFLOW.md](WORKFLOW.md) — **branch → work → prove → ship, as one
  sequence.** The page to follow; the others are reference behind it.
- [RELEASING.md](RELEASING.md) — the maintainer procedure, start to finish
- `scripts/shoddy.ps1` / `scripts/shoddy.sh` — the gate driver: `doctor`,
  `preflight`, `gate`, `package`, `publish`, `release`, `status`, `clean`,
  `selftest`. **`release VERSION` is the front door for shipping** - it runs
  strict `preflight`, the whole `gate` and `package`, and refuses to tag a
  commit with no green gate receipt against it. `shoddy-release.*` is the
  primitive it wraps and skips all three of those checks. Thin
  launchers over `scripts/gate/driver.mjs`, which holds the logic once so
  the twins cannot drift. **`preflight` is cheap (~3s) and read-only — run it
  before proposing anything release-shaped rather than reasoning about
  whether the tree is ready.** `gate --resume` is the expensive proof and
  skips what is already green for the current commit.
- [CONTRIBUTING.md](CONTRIBUTING.md) — day-to-day contribution process
- [release-notes/README.md](release-notes/README.md) — release-notes format and timing
- [SECURITY.md](SECURITY.md) — vulnerability reporting (not via public issues)
- `scripts/shoddy-branch.*` / `scripts/shoddy-release.*` — the actual
  automation. The twins offer the same verbs and `verify-twins.js` proves
  it, but they are not line-for-line identical and have drifted before, so
  read the one that matches the shell in use rather than the other
- `scripts/verify-docs.js` / `scripts/verify-errors.js` — pre-release gates
- `.github/workflows/ci.yml`, `release.yml`, `pages.yml` — what CI actually runs

Before doing anything release-shaped — cutting a branch, running the doc
gates, writing release notes, invoking either script, or explaining the
process to the user — open the relevant file(s) above rather than relying on
what a past session summarized.

<!-- fettler:begin -->
## Working on files here

Use the Fettler tools (`mcp__fettler__*`) for every file operation: find,
search, read, write, edit, move, copy, delete. They are the route, not an
option, and the built-in Read, Write, Edit, NotebookEdit, Grep and Glob are
denied so that habit cannot quietly take over.

**There is no working directory.** Paths resolve against declared trees, so
`cd` and `Set-Location` do nothing for Fettler and reaching for one is a sign
the wrong tool is being used. Ask `roots` first: it says which trees are open,
what may be done in each, and which one an unqualified path lands in.

**A tree may be read-only**, and a scope inside it may grant more or less than
the tree does. Running a declared task needs `execute`, which is never granted
by default.

`.fettler.json` and `.fettler.local.json` say what this tool may do - the
trees it may touch and the tasks it may run - and it does not write them.
A person edits those.

Run `fettle doctor` if anything here looks wrong.
<!-- fettler:end -->
