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
  (no launch.json needed): breakpoints, stepping, a call stack of your
  defs, Shoddy-shaped variables in three scopes (Locals, Globals, and
  the Value Stack), and a debug console that inspects bindings by
  name. Scribbler programs debug too — the window opens under the
  perch and stays live while you sit at a breakpoint. Runs `mill dap`
  under the hood.
- **Commands** (palette, editor context menu, and the ▶ run button):
  - **Shoddy: Run File** (`ctrl+r`) — `mill run` in the integrated
    terminal, so `Input` works.
  - **Shoddy: Weave File** — compile to a `.dll` beside the source.
  - **Shoddy: Build Machine from File** — compile a library to its
    `Shoddy.Machines.<Name>.dll`.
  - **Shoddy: Show Generated C#** — opens the weave's output as a C#
    document.

  Run, Weave, and Build Machine share one `shoddy mill` terminal. If a
  previous run is still sitting at an `Input` prompt, the next command's
  text goes to that prompt instead — finish or `Ctrl+C` the old run
  first.

## Setup

**Batteries included.** The `.vsix` carries its own mill and the whole
machine library, so installing the extension is the whole install — no
clone, no build, no `SHODDYLIB`. All it needs on the machine is the
[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
(the runtime, not the SDK).

The extension finds the mill via the `shoddy.millPath` setting; when
unset, it uses `bin/mill` in the workspace if present, then its own
bundled copy, else `mill` on your PATH. The bundled copy is portable
.NET: on Windows it runs `mill.exe`, and everywhere else it runs
`dotnet mill.dll` through the runtime you installed above. A workspace that builds its own
mill therefore keeps using it — which is what you want when the mill is
what you're changing.

Includes resolve the same way: beside the file first, then `SHODDYLIB`.
The extension points that at its bundled `machines/` when you haven't
set one yourself, so `Include "seq.shoddy"` works in a scratch file in
any folder. Set `SHODDYLIB` and it steps aside — a checkout stays
pinned to its own machines.

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
optional bump is `patch`/`minor`/`major` or an exact semver:

```sh
./build.sh vsix patch      # ./build.ps1 vsix patch on Windows
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
mill you build into `bin/` takes precedence over the bundled one. See
[the toolchain guide](https://shoddymills.github.io/shoddy/build.html)
for full workspace setup.
