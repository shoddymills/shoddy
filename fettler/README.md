# Fettler

File, search and edit tools for a model and a script. One program, two
front ends, no shell between it and the file.

The project, the lane and the MCP server are **Fettler**. The thing a
person or a script types is **`fettle`**, the imperative — because
`fettle move a.txt b.txt` reads as an instruction and `fettler move`
does not. Both spellings are correct and neither is a typo.

## What is here

| Project | What it holds |
|---|---|
| `Fettler/Core` | the operations: containment, text, writes, glob, find, search, edit, file operations, tasks |
| `Fettler/Cli` | the command-line front end — verbs, exit codes, and both output modes |
| `Fettler/Mcp` | the MCP front end — stdio JSON-RPC, hand-rolled over `System.Text.Json` |
| `fettle` | the executable, and the whole of it is wiring |
| `Fettler.Tests` | the proof; every item in R10 of the requirements is an assertion here |

## Building

```powershell
./build.ps1              # restore + build (Debug)
./build.ps1 test         # build + run Fettler.Tests
./build.ps1 release      # Release build
./build.ps1 publish 1.0.0 # self-contained single-file per OS
```

`build.sh` is the twin and takes the same arguments. **Unlike every
other lane here, this one needs nothing built first**: Fettler
references no other project in this repository, so there is no
`bin/mill.exe` to publish and no mill to weave.

## What is pinned

- **`net10.0`**, matching the rest of the repository.
- **No `ProjectReference` or `PackageReference` in what ships.** This is
  load-bearing and tested two ways: `FrontEndTests` reads the built
  assemblies' references, and `.github/workflows/fettler.yml` greps the
  project files. A constraint that is not tested lasts until somebody is
  in a hurry.
- **The MCP protocol is hand-rolled.** A preview-versioned SDK would be
  this tool's most volatile dependency sitting on its least stable
  layer; a few hundred lines is the cheaper exposure.

## Registering it with an MCP client

`serve` chooses the MCP front end. Selection by the name the executable
was invoked under is the Unix convention and rests on a mechanism not in
ordinary use on Windows, so it is a subcommand instead.

```json
{
  "mcpServers": {
    "fettler": {
      "command": "/path/to/fettle",
      "args": ["serve", "--root", "repo=/path/to/your/repository"]
    }
  }
}
```

**`--root NAME=PATH` is repeatable**, and more than one is the ordinary
case: an assistant working here has a repository, a sibling repository
and a scratch directory. Declare each and paths are written `name:path`;
declare one and paths are written bare, exactly as they always were.

```
--root repo=/work/shoddy --root notes=/work/shoddy-planning --root scratch=/tmp/scratch
```


**A tree can declare its own roots**, so nobody has to type them onto every
command. `fettle` takes the nearest `.fettler.json` at or above the current
directory whenever no `--root` is given:

```json
{
  "roots": {
    "repo": "."
  }
}
```

**Its paths are relative to the file, never to whoever is asking** — which
is the whole point of it. Read against the caller's working directory a
relative root names a different tree from every directory; read against the
file's own directory it names one tree from everywhere, so the same command
means the same thing three levels down and the file can be checked in.

`--config PATH` names a different file and `--no-config` turns the search
off. Any `--root` on the command line **replaces** the file rather than
adding to it, because merging would leave a caller unable to narrow the
boundary. A malformed configuration is refused, never fallen back from:
reverting to the current directory would move the boundary without saying
so, and usually widen it.

**Use absolute paths.** A relative root binds the boundary to whatever
directory `fettle` was launched from, so the same command means
different things from different places — and a caller that has to keep
correcting its working directory has gone back to doing shell work by
another name. `fettle roots` says what is declared:

```
$ fettle roots
repo        /work/shoddy  (default)
notes       /work/shoddy-planning
scratch     /tmp/scratch
```


Every path must resolve inside one of them. A move between two roots
works and says that it crossed, because moving a file between two trust
domains is something the caller is entitled to see.

## Arguments it will not guess at

**A flag no verb knows is refused, never ignored.** Ignoring one does not
produce a failure, it produces a *wrong answer*: a flag taking a value also
swallows the argument after it, so a misspelt `--gob "*.cs"` eats the
pattern and the search then runs against the whole tree and reports what it
found with complete confidence.

**A pattern may arrive as bytes rather than as an argument.**
`--pattern-file PATH` and `--pattern-stdin` take one pattern per line, the
same shape a `fettle-tasks` file uses for its arguments. The pattern is the
argument a shell is likeliest to spoil — quotes, backslashes and a leading
`--` all mean something to somebody first — and the damage is silent,
because a mangled pattern does not fail, it matches something else.

**A leading byte-order mark on a stream is taken off.** A BOM is a fact
about a file's encoding, not a character in its content, and it belongs at
the head of a file or nowhere. Windows PowerShell emits one whether asked
to or not; keeping it made a search pattern match nothing and spliced an
invisible character into the middle of a source file. A mark further along
is left alone.


## The verbs, and their exit codes

`find`, `search`, `read`, `write`, `edit`, `replace`, `new`, `mkdir`,
`move`, `copy`, `delete`, `exec`, `roots`, `tasks`, `run`, `batch` — and `serve`,
which is not an operation. **The CLI verb and the MCP tool name are the
same word**, and both front ends reach the operations through one
dispatcher, so a capability available to a model and not to a script is
unwritable rather than merely forbidden.

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | an unexpected internal fault |
| 2 | invalid — the request did not make sense |
| 3 | not-found |
| 4 | outside-root |
| 5 | target-exists |
| 6 | stale — the file changed since it was read |
| 7 | conflict — ambiguous or overlapping edits |
| 8 | refused — Fettler declined, for a stated reason |
| 9 | **denied — the operating system declined** |
| 10 | timed-out |

**8 and 9 are deliberately different.** Refused means Fettler declined;
denied means the platform did, for an operation Fettler was perfectly
willing to perform — a sandbox, a restricted account, a read-only
attribute, a file another process holds open. Collapsing them sends a
caller to debug a boundary that is working.

### Worked twins

The whole point of the CLI is that a `.ps1` and a `.sh` become
launchers, and neither redirects stderr anywhere:

```powershell
# scripts/list-types.ps1
$out = fettle search "^public sealed" --glob "src/**/*.cs" --json
if ($LASTEXITCODE -ne 0) { throw "search failed: $out" }
$out
```

```sh
# scripts/list-types.sh
out=$(fettle search '^public sealed' --glob 'src/**/*.cs' --json) || {
  printf '%s\n' "$out" >&2; exit 1;
}
printf '%s\n' "$out"
```

In machine-readable mode the complete result — **failures included** —
is on stdout. That is not a preference: Windows PowerShell 5.1 turns a
native program's redirected stderr into `NativeCommandError` records and
sets `$?` false even on exit code 0, so any design needing `2>&1` to
retrieve an answer is broken on the primary development platform.

## Declaring tasks

`run` executes only what a `fettle-tasks` file at the root declares, and
never composes a shell command. The format is **one argument per line**,
so there is no quoting syntax in it to get wrong — a file that cannot
express a quoting rule cannot carry a quoting bug.

```
# a comment
build:
  pwsh
  -File
  build.ps1

gate:
  node
  scripts/gate/driver.mjs
  gate
  --resume
  cwd: .
```

**That file is trusted input, and this says so out loud.** Containment
guards paths; it does not and cannot guard the command list, which is
read from a file inside the very root Fettler was pointed at. Whoever
can write it chooses what a caller — a model included — can execute.
The alternative is an allowlist maintained outside the repository, which
puts the declaration somewhere the repository's own contributors cannot
change alongside the build it belongs to. Point Fettler at a tree you
trust.

## What it deliberately does not do

Version control. Networked transport. A trash or undo store. Binary
editing. Language-aware editing. Permissions beyond the one bit git
tracks. Creating links. Setting a timestamp to a value the caller names.

Each of those is refused in the requirements with the reasoning
recorded, so a later reader does not re-open the question by accident.

## Where the name comes from

A **fettler** kept the carding engines in order — stripping matted fibre
and dirt out of the card clothing, cleaning, setting, repairing. He made
no cloth. His whole job was keeping the machines that made cloth in a
state where they could, which is this tool's relationship to everything
around it: it compiles nothing, runs nothing, and produces no program of
its own. *To fettle* is the plain English for it — to put right, to make
ready — and it survives in ordinary use as *in fine fettle*, so the name
carries its own meaning without a glossary.
