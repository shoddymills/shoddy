# Release notes

One file per release, named for its tag: `v1.2.0` → `release-notes/v1.2.0.md`.

The whole file becomes the body of the GitHub Release, rendered as markdown. Don't
repeat the version as a heading — the release is already titled with it.

**Write it before you cut the release.** The
[Release workflow](../.github/workflows/release.yml) checks out the tag and reads only
what that commit contains, so notes committed afterwards are invisible to it. The
sequence is: run the doc review (`node scripts/verify-docs.js` — the docs must match
the sources before the notes describe them), write the file, commit it on your feature
branch, ship the feature, then run `scripts/shoddy-release.sh X.Y.Z`. The release script prints whether it found the
file before it asks you to confirm — if it says MISSING, answer `n` and go write it.

If the file is absent the release still publishes, with a body generated from the
`--no-ff` merge subjects since the previous tag. That's a safety net, not a plan: it
reads like a list of branch names.

Full procedure: [RELEASING.md](../RELEASING.md).

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
