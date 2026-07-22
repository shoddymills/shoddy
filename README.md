# Shoddy
 
*Useless Things Made Useful Through Skill*

A purely functional BASIC. Friendly typed syntax on the surface —
parenthesized calls, infix operators, `Let`, records, `Select Case`
pattern matching — compiled onto a concatenative (Forth/Joy-style) stack
core that remains a legal dialect. Python's layout, BASIC's keywords,
Joy's soul.

```basic
Include "machines/stats.shoddy"

Type Student
    Name  As String
    Score As Number

Def Grade(s As Student) As String
    Select Case Score(s)
        Case >= 90
            "A"
        Case 70 To 89
            "B-ISH"
        Case Else
            "F"

Def Main()
    Let class = { Student("ADA", 96), Student("LIN", 78) }
    Each(class, Fn(s) => Print(Name(s) & ": " & Grade(s)))
    Print(Average(Map(class, Score)))
```

Nothing mutates: `Let` binds once, updates return new values, iteration
is recursion and `Map`/`Filter`/`Fold`. And the whole surface desugars to
stack code you can also write directly: `Def Square ( Number -- Number )`
/ `Dup *`.

## The name

Shoddy takes its name from the shoddy trade of the West Riding of
Yorkshire. From 1813, the mills of Batley and Dewsbury did what the wool
trade thought impossible: they took worn-out rags, tore them back to
fiber in a grinding machine called the devil, blended the reclaimed
stock with new wool, and spun and wove it into cloth again — blankets,
working clothes, and the uniforms of half the world's armies. The
American Civil War made the word a slur; the mills went on making honest
cloth for a century regardless, until synthetic fibers ended the trade.
This language is built the same way: old ideas — BASIC, Forth, Joy,
ISAM — reclaimed, blended with new wool, and woven into something
useful. Hence the **mill** (the executable), its **machines** (the
libraries), and the tagline, meant literally. The full story is in
[doc/heritage.html](doc/heritage.html).

## Layout

| Path  | Contents                                                    |
|-------|-------------------------------------------------------------|
| `src/`| The .NET solution: `Shoddy.Runtime` · `Shoddy.Devil` (front-end) · `Shoddy.Mill` · `Shoddy.Compiler` · `Shoddy.Tests` |
| `bin/`| Build output — the published `mill` toolchain lands here after `dotnet publish` (not committed; build it, then run `bin/mill`) |
| `machines/`| Standard library (the machines): `seq` `str` `math` `matrix` `stats` `random` `neural` `money` `file` `recio` `dict` `isam` `net` `simplex` `mps` `clock` `vt100` `keys` `scribbler` `turtle` `plotter` `buzzer` |
| `doc/`| `index.html` (start here) · `guide.html` · `spec.html` · `quickref.html` · `machines/` (one page per machine) · `build.html` |
| `tst/`| `libtest.shoddy` (assertion suite) · `examples.shoddy` · `gradebook.shoddy` · `simplex.shoddy` · demos (`scribbler-` `turtle-` `plotter-` `buzzer-demo.shoddy`) · `golden/` (the constitution) |
| `vscode-shoddy/`| VS Code extension: highlighting, snippets, mill commands |

## Build and run

Requires the .NET 10 SDK. Build the mill first — it is not committed:

```
dotnet publish src/Shoddy.Mill -c Release -o bin   # build the mill into bin/
```

Then:

```
bin/mill run tst/examples.shoddy     # compile in memory and run
bin/mill run tst/gradebook.shoddy    # interactive demo
bin/mill run tst/libtest.shoddy      # assertion suite - ends ALL ASSERTIONS PASSED
bin/mill weave tst/simplex.shoddy    # compile; then: dotnet tst/simplex.dll
bin/mill machine machines/seq.shoddy # compile a library to a machine DLL

dotnet test src/Shoddy.Tests     # the golden conformance suite
```

To rebuild the mill: `dotnet publish src/Shoddy.Mill -c Release -o bin`
(a folder publish — the weaver references `Shoddy.Runtime.dll` from
disk when compiling generated code).

### Build wrapper

Thin convenience wrappers bundle the commands above — `build.ps1` on
Windows, `build.sh` on Linux/macOS/WSL (same subcommands):

```
./build.sh build                 # build the mill into bin/
./build.sh test                  # golden suite + libtest assertions
./build.sh run tst/examples.shoddy
./build.sh machines              # compile every machine to a DLL
./build.sh vsix                  # package the VS Code extension (.vsix)
./build.sh vsix patch            # bump version, then package
./build.sh clean
```

On Windows, run `build.cmd` in place of `./build.sh` (e.g.
`build.cmd build`). It's a shim that invokes `build.ps1` with an
execution-policy bypass, so the unsigned script runs regardless of the
machine's PowerShell policy.

To call `build.ps1` directly instead, either bypass the policy per run:

```
powershell -ExecutionPolicy Bypass -File .\build.ps1 build
```

or allow local unsigned scripts for your user once:

```
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

(If `AllSigned` is enforced by Group Policy on a managed machine, use
`build.cmd` or the per-run bypass above.)

## Documentation

- **doc/index.html** — the front door: every guide and reference,
  organized by what you came for.
- **doc/guide.html** — the beginner's guide; assumes no prior knowledge.
- **doc/spec.html** — the language specification, including the concatenative
  core (Appendix A) and design rationale (Appendix B).
- **doc/quickref.html** — one-page reference of every builtin word, type,
  and syntax form, plus a map of the machines.

## Language at a glance

- Types: Number, String, Boolean, Quotation (closures), List, Array, and
  user records via `Type`. No NULL. Booleans are not numbers.
- Purity: no assignment, no `goto`, no `for`; `Print`/`Input`/`Rnd` are
  the only effects, kept at the edges by convention.
- `Select Case` matches values, ranges (`4 To 10`), comparisons
  (`Is >= 90`), and destructures records with `Where` guards.
- `Include "FILE.SHODDY"` splices files (include-once, path relative to the
  including file).
- Errors carry source line numbers; `Assert` and `Error` are built in.

## License

Shoddy is licensed under the **Shoddy Language License 1.0.0** — the
[PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)
with an Additional Use Grant. In short:

- **Noncommercial use** (personal, research, education, evaluation) is free.
- **Commercial use is also free** if both you (the individual or entity) and
  the compiled application each earn **less than $50,000 USD** in gross annual
  revenue.
- Above either threshold you need a **Commercial Production License**.

See [LICENSE](LICENSE) for the full terms. To purchase a Commercial Production
License, contact Stephen Vincent Foster at **svfoster@gmail.com**.

Contributions are governed by the CLA in
[CONTRIBUTING.md](CONTRIBUTING.md).

## Status

A research-preview-grade toy with working depth: a 100%-compiled .NET
language — every program weaves to C# and compiles with Roslyn, either
in memory (`mill run`) or to an assembly on disk (`mill weave`) — with
tail-call optimization, separately-compiled library machines, a
self-hosted standard library, and a golden conformance suite graded
byte-for-byte. Type annotations are parsed and enforced at runtime;
static checking with stack-effect inference is the headline item of
future work, alongside a REPL and catchable errors.
