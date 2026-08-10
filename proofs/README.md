# proofs/ — Part C's proof projects

These are neither mills nor language tests: they are .NET **consumers**
of mills, and they exist to discharge acceptance criteria (C8, and
through it A7's Mode T and refusal proofs). `Shoddy.Tests`'s
`ProofTests` runs every one of them, so `dotnet test` — and core CI —
covers this tree.

| Project | Proves |
|---|---|
| `console-cs/` | Mode N with hand-built `ShoddyValue`s and a record round-trip; Mode N at engine scale through halifax's `HxDo`; Mode T through `RunWovenAsync`, folder + manifest, source unread; the resource-pairing fail-fast |
| `console-fs/` | The same mill from F#, no wrapper assembly (C4.1 — CLS compliance proven by use) |
| `negative/refused/` | A project granting nothing is refused halifax at configure time; the message names the capability, the mill, the manifest, and prints the exact `Grant` line to add |
| `negative/degraded/` | Optional capabilities ungranted: the build warns per seam and the calculator still counts, headless and silent |
| `floor/` | The C1.3 dependency floor: a capability-free mill's consumer links `Shoddy.Runtime`, `Shoddy.Hosting` and the woven DLLs and nothing else — asserted by name in `ProofTests` |
| `fixtures/pure/` | The capability-free mill itself (A3.3b): one record type, three words, a headless golden suite, `capabilities: {}` |

Everything references `src/` by `ProjectReference` and relative
`Import` (C7.1); nothing here needs a published package. The
`ShoddyToolPath` in each project points at the repo's own `bin/mill`,
so run `./build.ps1 build` (or `./build.sh build`) once before building
these directly.
