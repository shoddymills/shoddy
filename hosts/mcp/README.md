# hosts/mcp — Sparky, the reckoner as an MCP server

The MCP lane of the hosting initiative, and the first host whose *user*
is a model rather than a person. `Shoddy.Mcp` holds the reusable pieces
(the session loop, the headless canvas, the grounding index, the tools
and the protocol loop), `sparky` is the executable, `Shoddy.Mcp.Tests`
proves both without a window, a client or a network.

Named after Karen Spärck Jones (1935–2007), who gave information
retrieval inverse document frequency. Her subject was retrieving the
right material for a question, and half this server's job is retrieving
the right grounding for a word.

This folder has its own solution and workflow and depends one-way on
`src/` (B2.2). Nothing here may reference `Shoddy.Mill` or
`Shoddy.Compiler` (B2.4). The weave arrives through `ShoddyWeave`
(`src/Shoddy.Build`), driven by the repo's published `bin/mill.exe` —
build that first:

```
./build.ps1            # from the repo root: publishes bin/mill
cd hosts/mcp
./build.ps1            # restore + build
./build.ps1 test       # and prove it
./build.ps1 publish    # the per-OS release archives, in artifacts/publish/
```

`publish` cuts what a release attaches: a self-contained single-file
sparky per OS, so the far end needs no repo and no .NET install. The
Release workflow runs it and puts one archive per OS on the GitHub
Release, beside the extension; `mcp.yml` cuts the same archives on
every push and drives the unpacked Linux binary over stdio, so the
target cannot rot between releases.

## The projects

| Project | Holds |
|---|---|
| `Shoddy.Mcp` | `SparkySession`, `SparkyCanvas`, `SparkyGrounding`, `SparkyTools`, `SparkyServer` |
| `sparky` | the executable: arguments, the root, the stdio streams |
| `Shoddy.Mcp.Tests` | the fold, isolation, capture, silence, concurrency, startup — no window, no client, no network |

`Shoddy.Mcp` is plain `net10.0` with no platform head and no third-party
package: the protocol is hand-rolled over `System.Text.Json`. A
preview-versioned protocol SDK would become this repository's most
volatile dependency and would sit on its least stable layer, so the few
hundred lines are the cheaper of the two.

The `sparky` project carries the `ShoddyMill` item. `Shoddy.Mcp.Tests`
carries the same one, so the tests run against the same woven mill the
executable ships.

## Registering it with a client

Sparky is a **local stdio server**: the client launches it as a child
process and speaks JSON-RPC to it, one message per line, on stdin and
stdout. Nothing listens on a socket. Every client needs the same two
facts:

```
sparky --root PATH
```

`--root` is where the session's files live — `sparkyrc`, anything `save`
writes, any chart `PLOTSAVE` writes. Omit it and the server uses
`LocalApplicationData/Shoddy/Sparky`, creating it if absent. Name one
that does not exist and the server refuses to start and says which path
it could not find: a root you named is one you meant, so a typo is
reported rather than materialised.

Client configuration formats move faster than this file does. The
contract above does not, so if a form has drifted, check that client's
own documentation and give it the same command and arguments.
[`docs/mills/sparky.html`](../../docs/mills/sparky.html) carries the
current shapes for Claude Desktop, Claude Code and VS Code, together with
the grounding prompt for anywhere without a server to ask.

Two lines on stdin check it without any client at all:

```sh
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"eval","arguments":{"lines":["{ 12500 13100 11900 } MEAN"]}}}' \
  | sparky --root /tmp/sparky
```

The second answer contains `[stack] x: 12500`.

**Stdout is the protocol and nothing else may write to it.** The engine's
`PRINT` goes to each session's own writer, never the console, and every
diagnostic here goes to stderr. A stray line on stdout is a parse error
at the client.

## Toolchain pins

| Item | Pinned where | Value this lane was built with |
|---|---|---|
| .NET SDK | `global.json` (repo root) states the floor; CI asks for `10.0.x` | 10.0.302 |
| packages | — | none; `System.Text.Json` ships with the framework |

This lane has no workload, so it does not read `global.json` directly the
way the MAUI lane does — the root file is a floor rather than a lock, and
any 10.x the runner installs satisfies it.

## What it does not do

Sound, a live window, an event loop, any polling word, remote or
networked transport, multi-tenancy. See
[`CAPABILITIES.md`](CAPABILITIES.md) for what sits behind each capability
it does grant, and why three seeds are not folded at all.
