# Release notes

One file per release, named for its tag: `v1.2.0` → `release-notes/v1.2.0.md`.

The whole file becomes the body of the GitHub Release, rendered as markdown. Don't
repeat the version as a heading — the release is already titled with it.

**Write it before you tag.** The
[Release workflow](../.github/workflows/release.yml) checks out the tag and reads only
what that commit contains, so notes committed afterwards are invisible to it. The
sequence is: run the doc gates (`./build.ps1 check` — the docs must match the sources
before the notes describe them), write the file, commit it on your feature branch,
merge the pull request, then ship with `scripts/shoddy-ship.ps1 X.Y.Z` — which refuses to
tag without the file, so a forgotten one is a re-run rather than a bad release.

If a tag is pushed by hand without the file, the release still publishes, with a
body generated from the commit subjects since the previous tag. That's a safety
net, not a plan: it reads like a changelog nobody wrote.

Full procedure: [WORKFLOW.md](../WORKFLOW.md); the reasoning is in
[RELEASING.md](../RELEASING.md).

## What to write

Lead with what a user notices, not what changed in the tree. New machines and
builtins, language surface changes, fixed bugs that bit someone, breaking changes
first and unmissable. Link `docs/` pages for anything that needs more than a line.

```markdown
## Added
- `machines/foo.shoddy` — one line on what it's for.

## Changed
- `Select Case` now destructures nested records.

## Fixed
- Line numbers were off by one in errors raised inside `Include`d files.
```
