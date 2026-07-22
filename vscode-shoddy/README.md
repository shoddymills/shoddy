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

The extension finds the mill via the `shoddy.millPath` setting; when
unset, it uses `bin/mill` in the workspace if present, else `mill` on
your PATH.

### Install the packaged .vsix (recommended)

A packaged `.vsix` ships in this folder. From here:

```sh
code --install-extension vscode-shoddy-0.9.2.vsix
```

The extension then appears in the Extensions view and can be updated
or uninstalled from there like any other. (If `code` isn't on your
PATH: `Cmd+Shift+P` → **Shell Command: Install 'code' command in
PATH**.)

### Rebuild the .vsix and bump the version

After editing the extension, repackage. `vsce` bumps the version in
`package.json` for you — `patch`/`minor`/`major` or an exact semver:

```sh
npm install -g @vscode/vsce
vsce package patch --no-git-tag-version   # e.g. 0.9.1 -> 0.9.2
```

To force a completely clean reinstall — old copy removed, cache
overwritten (the extension ID is `shoddy-mill.vscode-shoddy`):

```sh
code --uninstall-extension shoddy-mill.vscode-shoddy   # remove the old install
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

The mill, built from source into `bin/` (`./build.sh build`, or
`./build.ps1 build` on Windows). See `doc/build.html` in the Shoddy
repository for full workspace setup.
