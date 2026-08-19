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
- **No `ProjectReference`, ever.** That is the part carrying the reason:
  Fettler shares this repository, its release and its version number with
  what else lives here, and shares nothing else.
- **A `PackageReference` only from a tested allowlist.** Each entry must
  meet all of: a permissive licence (MIT, Apache-2.0, BSD) and never
  copyleft; pure managed with no native assets, because `fettle` publishes
  self-contained and single-file per OS; a minimal transitive closure; and
  active maintenance. The allowlist today is one package, **PdfPig**
  (Apache-2.0, pure managed, zero transitive dependencies), and it is
  there because the assistant's own reader is denied outright — which
  makes `fettle` the only reader, so a developer holding a PDF would
  otherwise be stranded. **Nothing else in this repository uses it** — the
  mill, the machines, the extension and sparky have no PDF component and no
  dependency on one — and `fettle` ships as its own archive carrying copies
  of `NOTICE` and `LICENSE`, so the Apache-2.0 and MIT obligations reach
  whoever downloaded it rather than stopping at the repository.
- **The allowlist is tested from outside.** `FrontEndTests` reads the
  built assemblies' references and names all seven PdfPig assemblies
  explicitly, so adding a package means changing a test on purpose rather
  than watching one stop failing. A constraint that is not tested lasts
  until somebody is in a hurry.
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
      "command": "fettle",
      "args": ["serve"]
    }
  }
}
```

**No root or config flag appears there, and none can.** The server learns
its trees at launch, from the `.fettler.json` in the folder it starts in,
before the model has said anything — and no tool schema carries a way to
name a different one. **Every call then re-reads that same file**,
overlay included, so an edit binds on the very next request: a granted
task appears, a revoked permission takes, and no restart is involved. A
malformed edit refuses every request, naming the fault, until it is
fixed --glob never falling back to the last good reading, since a boundary
held wider than the file states is the one direction this must never
fail in. Deleting the file refuses too, rather than searching again:
the file found at launch is the file, for the life of the server.

**A tree declares its own boundary**, so nobody types one onto every
command. `fettle` takes the nearest `.fettler.json` at or above the
current directory:

```json
{
  "trees": {
    "work": { "path": "." },
    "requirements": {
      "path": "../shoddy-requirements",
      "can": ["list", "read"],
      "scopes": {
        "backlog":           { "can": ["list", "read", "create", "update", "rename"] },
        "backlog/cancelled": { "can": [] }
      }
    }
  }
}
```

**Its paths are relative to the file, never to whoever is asking** — which
is the whole point of it. Read against the caller's working directory a
relative path names a different tree from every directory; read against
the file's own directory it names one tree from everywhere, so the same
command means the same thing three levels down and the file can be checked
in.

The seven permissions are `list read create update rename delete execute`.
The tree the file sits in gets all but `execute`; **every other tree gets
`list read`** unless `can` says more; **`execute` is never a default
anywhere**. A scope *replaces* what its tree grants rather than adding to
it, so it can take a permission away — and a scope with no `list` is not
there at all as far as `find` and `search` are concerned.

**`--root PATH` grants `list read` and nothing else, with no override.** It
stays because the server has to be launched somehow and ad-hoc reading is
useful; it cannot hand anybody write access, and --glob having no file behind
it --glob it is the one boundary a running server never re-reads. To write to a tree you put a
file in it, which is a deliberate act by a person inside the tree it
governs — and the act a caller cannot perform, because `.fettler.json`
and `.fettler.local.json` are refused by every write path at every
permission level. That refusal has its own outcome, `governed`, and its
own exit code, 11. Two names cover the trees and the tasks alike, since
one file declares both.

The same refusal covers the files that say what the **assistant** may do:
`.mcp.json`, `.claude/settings.json`, `.claude/settings.local.json` and
`.vscode/mcp.json`. No boundary has to be breached to reach one, because
a project's assistant configuration lives in the project - so the file
governing the assistant and a file the assistant may write were the same
file until this existed. A model that can edit its own deny list has no
deny list. Matched on the last two segments, so a bare `settings.json`
and `.vscode/settings.json` stay ordinary; `CLAUDE.md` is left out
because it persuades rather than permits. `setup` still writes all of
them, because it uses plain file IO and resolves no path through the
boundary.

## A write that would put a secret on disk

Refused, with its own outcome `credential` and its own exit code, 12.
**It judges the diff, not the file**: only a credential absent from the
previous content stops a write, so a config that already holds a key
stays editable - which is the difference between a rule people keep and a
rule people switch off.

Two tiers. Structural patterns match credentials whose shape their issuer
fixed: AWS key ids, GitHub and Slack tokens, Stripe live keys, Google API
keys, PEM private-key headers. The second tier is a guess and gated hard
because of it - a secret-shaped name assigned a long, high-entropy,
whitespace-free value that is not a reference, so an empty password and a
value naming an environment variable both pass.

**The refusal never repeats what it found.** A line number and a detector
name only: a message naming the secret would write it into the log and
the transcript, which is worse than the write it was refusing.

**`--allow-credential` is offered by the command line and by nothing
else.** No MCP tool carries it and a test asserts none ever will, for the
same reason `setup` is never offered: deciding a high-entropy string is
not a secret is a judgement about your own file, and a person makes it.

**It is a safety net, not a boundary.** Base64 the value or split the
string and every rule here is defeated, by accident as easily as on
purpose. It stops the ordinary accident of pasting a real key into a
config, not an adversary.

`--config PATH` names a different file and keeps full expressiveness.
`--no-config` turns the search off. Any `--root` **replaces** the file
rather than adding to it, because merging would leave a caller unable to
narrow the boundary. A `.fettler.local.json` beside the checked-in file
*is* merged over it — that is one tree's configuration layered on itself
rather than a caller overriding a tree — and it is gitignored, so a
machine's own trees need not be checked in.

**Declaring nothing at all is a refusal**, not a fallback to the current
directory. A working directory usually sits *above* the tree somebody
meant, so the old fallback silently widened the boundary at exactly the
moment nobody had stated one.

A malformed configuration is refused, never fallen back from, and the old
`roots` shape is refused with the migration named rather than
reinterpreted — a root granted everything, and reading it as a tree would
grant more than the file says.

`fettle roots` answers what is declared, what may be done in each, and
which file said so:

```
$ fettle roots
work          /work/shoddy  (default)
              can: list read create update rename delete
requirements  /work/shoddy-requirements
              can: list read
              backlog  can: list read create update rename
              backlog/cancelled  can: nothing

declared by /work/shoddy/.fettler.json
```

Every path must resolve inside one of them. A move between two trees works
and says that it crossed, because moving a file between two trust domains
is something the caller is entitled to see.

## Is it wired up?

```
fettle doctor
```

Two jobs in one verb: whether Fettler is registered with each assistant
client — Claude Code, Claude Desktop, VS Code Copilot, the GitHub Copilot
agent — at both the global and the repository level, and what on this
machine still lets the boundary be gone round. It never writes, never
prints a secret, and reads only a compiled-in list of well-known
configuration paths. `fettle setup CLIENT --local` writes the wiring;
`--dry-run` shows it first. See `docs/fettler-setup.html`.


## Line endings

**Text an edit brings in is rewritten into the file's own line endings.**
A caller composing a replacement cannot be expected to know a file's
endings, and on Windows the shell decides for them — a PowerShell
here-string is CRLF whatever the file is. Splicing that in verbatim leaves
a file disagreeing with itself by one line, which nothing notices until a
version control system does.

**Text already in the file is left exactly as it was**, so a mixed file
read and written back unchanged is still byte-identical. Only what the
edit brings in is made to match.

**`read` says when a file disagrees with itself**, marking it `(MIXED)`
next to the dominant ending, and setting `line_ending_mixed` in the
machine-readable answer. Reporting only the dominant ending is what hid a
stray CRLF here for two commits.

## Arguments it will not guess at

**A flag no verb knows is refused, never ignored.** Ignoring one does not
produce a failure, it produces a *wrong answer*: a flag taking a value also
swallows the argument after it, so a misspelt `--gob "*.cs"` eats the
pattern and the search then runs against the whole tree and reports what it
found with complete confidence.

**A pattern may arrive as bytes rather than as an argument.**
`--pattern-file PATH` and `--pattern-stdin` take one pattern per line —
one line is one string, the same idea a task's `run` array states as one
element per argument. The pattern is the
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
`move`, `copy`, `delete`, `exec`, `extract`, `roots`, `tasks`, `run`,
`batch`, `doctor`, `setup` — and `serve`, which is not an operation.
**The CLI verb and the MCP tool name are the same word**, and both front
ends reach the operations through one dispatcher, so a capability
available to a model and not to a script is unwritable rather than merely
forbidden.

**`read --tail N`** is the end of a file, counted backwards. A log is the
one file read backwards, and without it reaching the end of one costs two
calls — a read to learn the line count, then arithmetic to name a range.
A tool that cannot cheaply answer "what did it just say" sends its caller
to a shell, which is the thing this one exists to make unnecessary. It is
not combinable with `--from`/`--to`: those are a different question, and
answering one of them by precedence is a wrong answer rather than an
error.

**Archives are read and never written.** A `.zip`, `.tar`, `.tar.gz` or
`.tgz` reads as its manifest — every member, its size, and whether it
carries the execute bit — and `--member NAME` reads one entry out of it,
decoded by the ordinary rules, without unpacking anything. A lone `.gz` is
one file wearing a coat and reads as what is underneath. Neither
`System.IO.Compression` nor `System.Formats.Tar` is a package: both are in
the shared framework, so the R1.2 allowlist is untouched.

**`extract ARCHIVE --into DIR`** is the one that writes, and it is refused
*whole* or not at all. Every member resolves through the boundary before a
byte is written, so zip slip is an ordinary outside-root refusal; a member
escaping only the *destination* (`out/../elsewhere.txt`, which the
boundary is perfectly happy with) is refused too; a link member is refused
rather than skipped, because a skip produces an extraction that looks
complete and is not; and the executable bit is carried, which is R6.9 and
the reason the release tars are cut on a Linux runner. Producing an
archive is deliberately not here: that is a build output, and building is
what a declared task and `run` are for.

**`setup` is the one deliberate exception**, and a test asserts it stays
one. It writes the client configuration files — including the one naming
the command that launches the server — so a model holding it could point
its own boundary at a different binary, or grant back the permissions the
deny list removes. Parity is a rule about capability, not about reach:
scaffolding a machine is a person's act, performed once, in a terminal.
`doctor` *is* offered, because a model diagnosing its own wiring beats
asking somebody to open a terminal for it.

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
| 11 | **governed — the path names a file that says what this tool may do** |
| 12 | **credential - the write would have ADDED a secret the file did not already carry** |

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

`run` executes only what the `.fettler.json` declares, and never composes
a shell command:

```json
{
  "trees": {
    "work": { "path": ".", "can": ["list","read","create","update","rename","delete","execute"] }
  },
  "tasks": {
    "build": { "run": "pwsh -File build.ps1" },
    "gate":  { "run": "node scripts/gate/driver.mjs gate --resume", "cwd": "." },
    "sign":  { "run": "signtool sign /f \"C:\\keys\\my cert.pfx\" bin/app.exe" }
  }
}
```

**The whole grammar:** whitespace separates arguments; a double-quoted
span is one argument, and the quotes are removed; two double quotes
inside a quoted span are one literal quote; **a backslash is never an
escape**, so a Windows path is written once rather than doubled. Nothing
else is special — no single quotes, no variables, no globbing, no
redirection, no operators — and an unclosed quote is refused. The string
is split once and the program is launched with the resulting list.

**A task takes no arguments from its caller.** `run` accepts a name and a
timeout and nothing else; an extra word is refused rather than dropped,
on the command line and over MCP alike. What runs is exactly what the
file says, and a variant is a second declared task.

`cwd` is optional and defaults to the top of the default tree; it is
checked for containment like any other path. **A relative declared path
is written with forward slashes** — the git convention — and a backslash
in one is refused, since on macOS and Linux it is an ordinary character
in a name rather than a separator. An absolute path names one machine
either way and is left alone.

**That declaration is trusted input, and this says so out loud.**
Containment guards paths; it does not and cannot guard a command list,
which is read from a file inside the very root Fettler was pointed at.
Whoever can write it chooses what a caller — a model included — can
execute. The alternative is an allowlist maintained outside the
repository, which puts the declaration somewhere the repository's own
contributors cannot change alongside the build it belongs to. Point
Fettler at a tree you trust.

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
