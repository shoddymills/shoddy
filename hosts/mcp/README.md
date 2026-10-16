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

This folder has its own solution and depends one-way on `src/` (B2.2).
Nothing here may reference `Shoddy.Mill` or `Shoddy.Compiler` (B2.4);
`scripts/verify-lanes.js` checks that. The weave arrives through
`ShoddyWeave` (`src/Shoddy.Build`), driven by the headless mill, which
the lane's projects build themselves. The lane's `build.ps1` (and
`build.sh`) runs the repo root's `build` first:

```
cd hosts/mcp
./build.ps1            # restore + build
./build.ps1 test       # and prove it
./build.ps1 release    # Release build, and its closure (verify-closure.js)
./build.ps1 publish    # the per-OS release archives, in artifacts/publish/
./build.ps1 install    # this machine: build, prove, install, register with Claude Code
```

`install [V]` is the one command from a change to an agent using it. It
publishes the single file for this platform, drives it over stdio and
stops if it does not answer, copies it to `SPARKY_HOME` (default
`%LOCALAPPDATA%\Shoddy\sparky` on Windows, `~/.local/share/shoddy/sparky`
elsewhere), and registers that path with Claude Code as `sparky`,
replacing any earlier registration. The path never changes, so
re-running it is the upgrade; start a new Claude Code session to pick it
up. It registers at project scope, in the `.mcp.json` of the folder you
ran install from, with `--root .`, so sparky and fettle both root in
that repo. An absolute `SPARKY_ROOT` registers at user scope with that
root instead. Without the `claude` CLI it installs and prints the
command to give your client.

`publish [V]` cuts what a release attaches: a self-contained single-file
sparky per OS, so the far end needs no repo and no .NET install. V names
the archives and is the version every binary reports. It then checks the
execute bit on every unix archive and drives this platform's binary over
stdio (`scripts/verify-shipped.js`). The Release workflow runs it at the
tag's version; ci.yml's `mcp` job and `build prove` run it at a
throwaway one on every push, so the target cannot rot between releases.

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
| .NET SDK | `global.json` (repo root); every CI job and the Release workflow install it through `setup-dotnet`'s `global-json-file` | 10.0.302 |
| packages | — | none; `System.Text.Json` ships with the framework |

## What it does not do

Sound, a live window, an event loop, any polling word, remote or
networked transport, multi-tenancy. See
[`CAPABILITIES.md`](CAPABILITIES.md) for what sits behind each capability
it does grant, and why three seeds are not folded at all.
