# Shoddy for VS Code

*Useless Things Made Useful Through Skill*

Language support for Shoddy — the purely functional BASIC — wired to
the **mill** (the .NET toolchain).

## Features

- **Highlighting** — a real TextMate grammar: keywords, the builtin
  vocabulary, quotations `[ ... ]`, list literals `{ ... }`,
  stack-effect signatures `( a b -- c )`, `Rem` and `'` comments —
  all case-insensitive, as the language is.
- **Indentation** — `Def` / `Type` / `If ... Then` / `Select Case` /
  `Case` open blocks; `Else` and `Case` dedent to their owners.
- **Snippets** — `def`, `sdef`, `main`, `type`, `sum`, `if`, `select`,
  `casew`, `fn`, `let`, `each`, `include`.
- **Native debugging — the perch.** Press `F5` on any `.shoddy` file
  (no launch.json needed). The program runs as it does under `mill run`,
  in a terminal window of its own, outside VS Code (the system's
  terminal, or the one `terminal.external.*` names), with its keyboard
  and screen, from its own folder. Breakpoints anywhere, machines included, with conditions, hit
  counts and logpoints; a stop at any runtime error; pause; stepping
  into, over and out of any Def, a machine's included; a call stack of
  your defs; Shoddy-shaped variables in three scopes (Locals, Globals,
  and the Value Stack), each of which can be set; and a Debug Console,
  Watch and hover that evaluate any Shoddy expression. Scribbler
  programs debug too — the window opens under the perch and stays live
  while you sit at a breakpoint. A launch.json adds `args`, `allowNet`
  and `cwd`. Under the hood, `mill dap` is the debug adapter VS Code
  talks to, and the window runs `mill run --perch`, the program's own
  process, which connects back to it.
- **Commands** (palette, editor context menu, and the ▶ run button):
  - **Shoddy: Run File** (`ctrl+r`) — `mill run` in a terminal window
    of its own, outside VS Code, so `Input` and `InKey` work at full
    size. The same as Run → Run Without Debugging (`Ctrl+F5`).
  - **Shoddy: Weave File** — compile to a `.dll` beside the source.
  - **Shoddy: Build Machine from File** — compile a library to its
    `Shoddy.Machines.<Name>.dll`.
  - **Shoddy: Show Generated C#** — opens the weave's output as a C#
    document.

  **A program runs from its own folder**, whichever folder the
  workspace is opened on, so a relative path in it such as
  `files/words.txt` finds the file beside the program. That holds for
  `F5` and for every command, in a checkout or out of one. A launch.json
  can name another folder with `"cwd"`; a relative one is taken against
  the workspace folder.

  Run, Weave, and Build Machine share one terminal per folder, named
  `shoddy mill: FOLDER`. If a previous run there is still sitting at an
  `Input` prompt, the next command's text goes to that prompt instead —
  finish or `Ctrl+C` the old run first.

## Setup

**Batteries included.** The `.vsix` carries its own mill and the whole
machine library, so installing the extension is the whole install — no
clone, no build, no `SHODDYLIB`. All it needs on the machine is the
[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
(the runtime, not the SDK).

The extension finds the mill via the `shoddy.millPath` setting; when
unset, it uses the mill the workspace has built
(`artifacts/bin/Shoddy.Mill/release/mill`) if present, then its own
bundled copy, else `mill` on your PATH. The bundled copy is portable
.NET: on Windows it runs `mill.exe`, and everywhere else it runs
`dotnet mill.dll` through the runtime you installed above. A workspace that builds its own
mill therefore keeps using it — which is what you want when the mill is
what you're changing.

Includes resolve the same way: beside the file first, then `SHODDYLIB`,
then the `machines/` beside the mill. When you haven't set `SHODDYLIB`
yourself, the extension points it at the sources that go with the mill
in use: a checkout's own `machines/` for the mill it built, the bundled
`machines/` otherwise. So `Include "seq.shoddy"` works in a scratch
file in any folder, and a mill in a checkout links the library the
checkout built. Set `SHODDYLIB` and it steps aside.

### Install the packaged .vsix (recommended)

The `.vsix` isn't committed — download the latest one from
[the latest release](https://github.com/shoddymills/shoddy/releases/latest),
then in VS Code: Extensions view → `···` menu → **Install from VSIX…** →
pick the file you downloaded.

(To build one yourself instead: `./build.sh vsix` from the repo root, or
`./build.ps1 vsix` on Windows — it lands in this folder.)

The extension then appears in the Extensions view and can be updated
or uninstalled from there like any other.

### Rebuild the .vsix and bump the version

After editing the extension, repackage from the repo root — that stages
a fresh mill and freshly built machines into the package first. The
optional argument is `patch`/`minor`/`major`, which bumps the version in
`package.json`, or an exact `X.Y.Z`, which packages this build as that
version and changes no file:

```sh
./build.sh vsix patch      # ./build.ps1 vsix patch on Windows
./build.sh vsix 2.10.0     # mill and package both report 2.10.0
./build.sh install 2.10.0  # install vscode-shoddy-2.10.0.vsix
```

Running `vsce package` by hand in this folder still works, but it packs
whatever `mill/` and `machines/` were last staged (or ships neither, if
they never were).

To force a completely clean reinstall — old copy removed, cache
overwritten (the extension ID is `shoddymills.vscode-shoddy`):

```sh
code --uninstall-extension shoddymills.vscode-shoddy   # remove the old install
rm -f *.vsix                                           # drop stale packages
vsce package patch --no-git-tag-version                # repackage at a new version
code --install-extension vscode-shoddy-<new-version>.vsix --force
```

Then reload VS Code (`Cmd+Shift+P` → **Developer: Reload Window**), or
quit fully (`Cmd+Q`) if a previous install is still loaded. Bumping the
version is what makes VS Code pick up the new build; `--force` overwrites
an install at the same version.

### Hacking on the extension (no packaging)

Open this folder in VS Code and press **F5** to launch an Extension
Development Host with the extension loaded. Edits to the
grammar/snippets/commands take effect on the next **Developer: Reload
Window** in that host — no re-copy needed.

## Requirements

The [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Everything else — the mill and every machine — ships inside the `.vsix`.

To work on Shoddy itself you want the SDK and a checkout instead; the
mill you build under `artifacts/` takes precedence over the bundled one. See
[the toolchain guide](https://shoddymills.github.io/shoddy/build.html)
for full workspace setup.
