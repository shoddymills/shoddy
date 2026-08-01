# Shoddy

[![CI](https://github.com/shoddymills/shoddy/actions/workflows/ci.yml/badge.svg)](https://github.com/shoddymills/shoddy/actions/workflows/ci.yml)

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

## The showpieces

*Mungo Caverns, a purely functional reimagining of our favourite text
adventure — and Devil's Dust, the language's own name-story animated, with
a tuning console that goes to eleven.*

<table>
  <tr>
    <td align="center">
      <a href="https://shoddymills.github.io/shoddy/mills/mungo-caverns.html"><img src="docs/media/mungo-caverns.gif" alt="Mungo Caverns played in a terminal: into the well house, take the lamp, xyzzy to the debris room" width="100%"></a><br>
      <b>Mungo Caverns</b><br><sub>the cave crawl, purely functional · graded against the original's transcripts</sub>
    </td>
  </tr>
  <tr>
    <td align="center">
      <a href="https://shoddymills.github.io/shoddy/mills/devils-dust.html"><img src="docs/media/devils-dust.gif" alt="Devil's Dust running: the lever pulled to eleven, wool erupting, then the full stop raining the sky out" width="100%"></a><br>
      <b>Devil's Dust</b><br><sub>boids over the devil's drum · live console, machine-room drone</sub>
    </td>
  </tr>
</table>

## Two minutes to running

<table>
  <tr>
    <td width="55%" valign="middle">
      <b>One download is the whole toolchain.</b> The VS Code extension
      carries the mill and all twenty-two machines inside it — install the
      <a href="https://dotnet.microsoft.com/download/dotnet/10.0">.NET 10 runtime</a>
      (the runtime, not the SDK), install the
      <a href="https://github.com/shoddymills/shoddy/releases/latest">latest <code>.vsix</code></a>,
      open any folder, and run. Nothing to clone, nothing to build, no
      environment to set: a bare <code>Include "seq.shoddy"</code> resolves
      anywhere, and breakpoints, variables, and the call stack work out of
      the box. Details under <a href="#install">Install</a>, or
      <a href="https://shoddymills.github.io/shoddy/setup.html">the two-step setup guide</a>.
    </td>
    <td width="45%" align="center" valign="middle">
      <a href="https://shoddymills.github.io/shoddy/vscode.html"><img src="docs/media/vscode-debug.svg" alt="Debugging invaders in VS Code, stopped at the collision-scoring line" width="100%"></a><br>
      <b>Debug in VS Code</b><br><sub>breakpoints, variables, call stack</sub>
    </td>
  </tr>
</table>

## See it run — every mill

Complete programs woven from the machines — each split into a headless,
unit-tested pure core and a thin I/O shell. Click through for the full
write-up on each one, or start at
[the mills catalog](https://shoddymills.github.io/shoddy/mills/index.html).

<table>
  <tr>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/mungo-caverns.html"><img src="docs/media/mungo-caverns.svg" alt="Mungo Caverns in a terminal" width="100%"></a><br>
      <b>mungo-caverns</b><br><sub>the cave crawl, purely functional</sub>
    </td>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/devils-dust.html"><img src="docs/media/devils-dust.svg" alt="Devil's Dust: flocking wool and the tuning panel" width="100%"></a><br>
      <b>devils-dust</b><br><sub>boids with a console · goes to eleven</sub>
    </td>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/invaders.html"><img src="docs/media/invaders.svg" alt="Space Invaders in a scribbler window" width="100%"></a><br>
      <b>invaders</b><br><sub>graphical window · colour + sound</sub>
    </td>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/oregon.html"><img src="docs/media/oregon.svg" alt="The Oregon Trail in a terminal" width="100%"></a><br>
      <b>oregon</b><br><sub>the 1971 classic, reclaimed</sub>
    </td>
  </tr>
  <tr>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/pac-vt100.html"><img src="docs/media/pac-vt100.svg" alt="Pac-Man drawn with VT100 escapes" width="100%"></a><br>
      <b>pac-vt100</b><br><sub>arcade chase in the terminal</sub>
    </td>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/iris.html"><img src="docs/media/iris.svg" alt="Scatter plot of iris petal measurements, three species in three colours" width="100%"></a><br>
      <b>iris</b><br><sub>neural classifier · trains in 4s</sub>
    </td>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/demographics.html"><img src="docs/media/demographics.svg" alt="Neural income model chart" width="100%"></a><br>
      <b>demographics</b><br><sub>neural regression · the other head</sub>
    </td>
    <td width="25%" align="center" valign="top">
      <a href="https://shoddymills.github.io/shoddy/mills/simplex-from-mps.html"><img src="docs/media/simplex-blend.svg" alt="Terminal output solving the BLEND linear program" width="100%"></a><br>
      <b>simplex-from-mps</b><br><sub>83-var LP, solved from MPS</sub>
    </td>
  </tr>
</table>

## The machines — all twenty-two

The standard library, one folder of Shoddy source per machine — graphics,
sound, statistics, neural nets, an LP solver, keyed files, and TCP/IP,
batteries included. Full word references in
[the machines catalog](https://shoddymills.github.io/shoddy/machines/index.html).

<table>
  <tr>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/seq.html"><img src="docs/media/icons/seq.svg" alt="seq" width="100%"></a><br><b>seq</b><br><sub>Map, Filter, Fold and friends</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/str.html"><img src="docs/media/icons/str.svg" alt="str" width="100%"></a><br><b>str</b><br><sub>string helpers</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/math.html"><img src="docs/media/icons/math.svg" alt="math" width="100%"></a><br><b>math</b><br><sub>the derived math layer</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/matrix.html"><img src="docs/media/icons/matrix.svg" alt="matrix" width="100%"></a><br><b>matrix</b><br><sub>flat row-major matrices</sub></td>
  </tr>
  <tr>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/stats.html"><img src="docs/media/icons/stats.svg" alt="stats" width="100%"></a><br><b>stats</b><br><sub>means to real p-values</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/random.html"><img src="docs/media/icons/random.svg" alt="random" width="100%"></a><br><b>random</b><br><sub>shuffles, samples, ranges</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/neural.html"><img src="docs/media/icons/neural.svg" alt="neural" width="100%"></a><br><b>neural</b><br><sub>feed-forward nets, two heads</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/money.html"><img src="docs/media/icons/money.svg" alt="money" width="100%"></a><br><b>money</b><br><sub>exact cents, no drift</sub></td>
  </tr>
  <tr>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/file.html"><img src="docs/media/icons/file.svg" alt="file" width="100%"></a><br><b>file</b><br><sub>line-oriented text I/O</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/recio.html"><img src="docs/media/icons/recio.svg" alt="recio" width="100%"></a><br><b>recio</b><br><sub>fixed-size binary records</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/dict.html"><img src="docs/media/icons/dict.svg" alt="dict" width="100%"></a><br><b>dict</b><br><sub>key/value over Pair lists</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/isam.html"><img src="docs/media/icons/isam.svg" alt="isam" width="100%"></a><br><b>isam</b><br><sub>keyed files, B+tree index</sub></td>
  </tr>
  <tr>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/net.html"><img src="docs/media/icons/net.svg" alt="net" width="100%"></a><br><b>net</b><br><sub>TCP/IP, non-blocking</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/simplex.html"><img src="docs/media/icons/simplex.svg" alt="simplex" width="100%"></a><br><b>simplex</b><br><sub>linear programming</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/mps.html"><img src="docs/media/icons/mps.svg" alt="mps" width="100%"></a><br><b>mps</b><br><sub>the LP file format</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/clock.html"><img src="docs/media/icons/clock.svg" alt="clock" width="100%"></a><br><b>clock</b><br><sub>timing and timestamps</sub></td>
  </tr>
  <tr>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/vt100.html"><img src="docs/media/icons/vt100.svg" alt="vt100" width="100%"></a><br><b>vt100</b><br><sub>terminal control codes</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/keys.html"><img src="docs/media/icons/keys.svg" alt="keys" width="100%"></a><br><b>keys</b><br><sub>key codes to GameKeys</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/scribbler.html"><img src="docs/media/icons/scribbler.svg" alt="scribbler" width="100%"></a><br><b>scribbler</b><br><sub>pixels in a window</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/turtle.html"><img src="docs/media/icons/turtle.svg" alt="turtle" width="100%"></a><br><b>turtle</b><br><sub>purely functional turtles</sub></td>
  </tr>
  <tr>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/plotter.html"><img src="docs/media/icons/plotter.svg" alt="plotter" width="100%"></a><br><b>plotter</b><br><sub>statistical charts</sub></td>
    <td width="25%" align="center" valign="top"><a href="https://shoddymills.github.io/shoddy/machines/buzzer.html"><img src="docs/media/icons/buzzer.svg" alt="buzzer" width="100%"></a><br><b>buzzer</b><br><sub>notes, MML tunes, three waves</sub></td>
    <td width="25%"></td>
    <td width="25%"></td>
  </tr>
</table>

## The name

Shoddy takes its name from the shoddy trade of the West Riding of
Yorkshire. From 1813, the mills of Ossett, Morley, and Wakefield did what
the wool trade thought impossible: they took worn-out rags, tore them back to
fiber in a grinding machine called the devil, blended the reclaimed
stock with new wool, and spun and wove it into cloth again — blankets,
working clothes, and the uniforms of half the world's armies. The
American Civil War made the word a slur; the mills went on making honest
cloth for a century regardless, until synthetic fibers ended the trade.
This language is built the same way: old ideas — BASIC, Forth, Joy,
ISAM — reclaimed, blended with new wool, and woven into something
useful. Hence the **mill** (the executable) and its **machines** (the
libraries). And the tagline is Ossett's own: *Useless Things Made Useful
Through Skill* renders that town's civic motto, *Inutile Utile Ex Arte*
("the useless made useful by skill"), meant here literally. The full story
is in [the heritage of the name](https://shoddymills.github.io/shoddy/heritage.html).

## Layout

| Path  | Contents                                                    |
|-------|-------------------------------------------------------------|
| `src/`| The .NET solution: `Shoddy.Runtime` · `Shoddy.Devil` (front-end) · `Shoddy.Mill` · `Shoddy.Compiler` · `Shoddy.Tests` |
| `bin/`| Build output — the published `mill` toolchain lands here after `dotnet publish` (not committed; build it, then run `bin/mill`) |
| `machines/`| Standard library (the machines): `seq` `str` `math` `matrix` `stats` `random` `neural` `money` `file` `recio` `dict` `isam` `net` `simplex` `mps` `clock` `vt100` `keys` `scribbler` `turtle` `plotter` `buzzer` |
| `mills/`| Complete example programs (the mills), each with its own build wrapper: `mungo-caverns` (Colossal Cave Adventure) · `oregon` (overland-trail game, after MECC's 1971 original) · `pac-vt100` · `invaders` · `demographics` (neural income model, regression) · `iris` (neural species classifier, ~4s to train) · `simplex-from-mps` (LP solver) |
| `docs/`| `index.html` (start here) · `guide.html` · `setup.html` (one-time machine setup) · `vscode.html` (editor) · `build.html` (the toolchain) · `stack.html` (the stack, traced) · `spec.html` · `quickref.html` · `machines/` and `mills/` (one page each) |
| `tst/`| `libtest.shoddy` (assertion suite) · `examples.shoddy` · `gradebook.shoddy` · `simplex.shoddy` · demos (`scribbler-` `turtle-` `plotter-` `buzzer-` `net-demo.shoddy`) · `golden/` (the constitution) |
| `vscode-shoddy/`| VS Code extension: highlighting, snippets, debugging, mill commands |

## Install

Most people want the VS Code extension. It carries its own mill and the whole
machine library, so it is the entire install — download the latest `.vsix` from
[the latest release](https://github.com/shoddymills/shoddy/releases/latest),
then in VS Code press **Ctrl+Shift+P** (**Cmd+Shift+P** on macOS), run
**Extensions: Install from VSIX…**, and choose the file you downloaded.

The only prerequisite is the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
(the runtime, not the SDK). Nothing to clone, nothing to build, no environment to
set: a bare `Include "seq.shoddy"` resolves in any folder you open. See
[the setup guide](https://shoddymills.github.io/shoddy/setup.html).

## Build from source

For working on the language itself, or to get `mill` on your command line.
Requires the .NET 10 SDK. Build the mill (it isn't committed), then run a
program and the tests:

```
dotnet publish src/Shoddy.Mill -c Release -o bin   # build the mill into bin/
bin/mill run tst/examples.shoddy                   # compile in memory and run
dotnet test src/Shoddy.Tests                       # golden conformance suite
```

Or use the build wrapper — `./build.sh <cmd>` on Linux/macOS/WSL,
`build.cmd <cmd>` on Windows — which bundles the common tasks: `build`,
`test`, `run FILE`, `machines`, `stage`, `vsix`, `clean`.

`vsix` packages a self-contained VS Code extension: it stages the mill and
every machine into the package, so installing the `.vsix` needs only the
.NET 10 *runtime* — no SDK, no checkout, no `SHODDYLIB`.

The full toolchain — weaving to an assembly, building library machines, and
the Windows PowerShell notes — is in [the toolchain guide](https://shoddymills.github.io/shoddy/build.html).

## Documentation

- [docs/index.html](https://shoddymills.github.io/shoddy/index.html) — the front door: every guide and reference,
  organized by what you came for.
- [docs/guide.html](https://shoddymills.github.io/shoddy/guide.html) — the beginner's guide; assumes no prior knowledge.
- [docs/spec.html](https://shoddymills.github.io/shoddy/spec.html) — the language specification, including the concatenative
  core (Appendix A) and design rationale (Appendix B).
- [docs/quickref.html](https://shoddymills.github.io/shoddy/quickref.html) — one-page reference of every builtin word, type,
  and syntax form, plus a map of the machines.

## Language at a glance

- Types: Number, String, Boolean, Quotation (closures), List, Array, and
  user records via `Type` — every field name becomes an accessor function
  (`Score(s)`), composable like any other (`Map(class, Score)`). No NULL:
  variant types (`Type Option = Some(v) | None`) make "maybe nothing" a
  value you pattern-match, not a check you can forget. Booleans are not
  numbers.
- Purity: no assignment, no `goto`, no `for`; `Print`/`Input`/`Rnd` are
  the only effects, kept at the edges by convention.
- `Select Case` matches values, ranges (`4 To 10`), comparisons
  (`Is >= 90`), and destructures records and variants (`Case Some(v)`,
  `Case None`) with `Where` guards.
- `Include "FILE.SHODDY"` splices files (include-once) — resolved beside the
  including file, then `SHODDYLIB`, then the machine library beside the running
  mill, so a bare `Include "seq.shoddy"` works from any folder.
- Errors carry source line numbers; `Assert` and `Error` are built in.

## License

Shoddy is free and open source under the **[MIT License](LICENSE)** — use it
for anything, including commercially, with attribution.

Third-party dependencies and the historical/homage example programs (OREGON,
the Pac-Man and Space Invaders homages, and a neural-net reference) remain
under their own licenses and their owners' trademarks. The Colossal Cave
Adventure port ([mills/mungo-caverns/](mills/mungo-caverns/)) derives from
Crowther & Woods via Eric Raymond's Open Adventure and is **BSD-2-Clause**; see
its [LICENSE](mills/mungo-caverns/LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Contributions are welcome under the MIT License; see
[CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md).
This project intends to join the [.NET Foundation](https://dotnetfoundation.org/).

## Status

Version 1.0, and the toolchain is real: a 100%-compiled .NET language —
every program weaves to C# and compiles with Roslyn, either in memory
(`mill run`) or to an assembly on disk (`mill weave`) — with tail-call
optimization, separately-compiled library machines, a self-hosted
standard library, and a golden conformance suite graded byte-for-byte.
The programs in `mills/` are not syntax demonstrations; they are
complete, working software.

Shoddy is young, and there is work still ahead. Type annotations are
parsed and enforced at runtime, but the mill now carries a weave-time
linter with stack-effect inference: every `Def`'s net effect is checked,
branches must agree, call sites are depth-checked against declared
arities (machine manifests carry their words' effects), and an unknown
word is a weave error rather than a runtime crash. Warnings never stop a
build (`--no-lint` silences them; `--lint-verbose` reports coverage), and
the checker skips what it cannot prove rather than guess. Full static
*type* checking remains ahead, alongside a REPL and catchable errors.
