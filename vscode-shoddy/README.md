# Shoddy for VS Code

*Making useless things useful, through skill.*

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
  name. Runs `mill dap` under the hood.
- **Commands** (palette, editor context menu, and the ▶ run button):
  - **Shoddy: Run File** (`ctrl+r`) — `mill run` in the integrated
    terminal, so `Input` works.
  - **Shoddy: Weave File** — compile to a `.dll` beside the source.
  - **Shoddy: Build Machine from File** — compile a library to its
    `Shoddy.Machines.<Name>.dll`.
  - **Shoddy: Show Generated C#** — opens the weave's output as a C#
    document.

## Setup

The extension finds the mill via the `shoddy.millPath` setting; when
unset, it uses `bin/mill` in the workspace if present, else `mill` on
your PATH.

### Install the packaged .vsix (recommended)

```sh
npx @vscode/vsce package --allow-missing-repository --skip-license
code --install-extension vscode-shoddy-2.0.0.vsix
```

The extension then appears in the Extensions view and can be updated
or uninstalled from there like any other. (If `code` isn't on your
PATH: `Cmd+Shift+P` → **Shell Command: Install 'code' command in
PATH**. If a previous install of this extension is loaded, quit VS
Code fully — `Cmd+Q` — before running the install.)

### Install as a plain copy (no tooling)

```sh
cp -R "$(pwd)" ~/.vscode/extensions/shoddy-mill.vscode-shoddy-2.0.0
```

Then reload VS Code (`Cmd+Shift+P` → **Developer: Reload Window**).
To uninstall, delete that folder. Repeat the copy after changing the
extension source.

### Symlink (for hacking on the extension)

```sh
ln -s "$(pwd)" ~/.vscode/extensions/shoddy-mill.vscode-shoddy-2.0.0
```

Edits to the grammar/snippets/commands take effect on the next
**Developer: Reload Window** — no re-copy needed.

## Requirements

The mill (`bin/mill` in the repo, or built from `src/`). See
`doc/VSCODE.md` in the Shoddy repository for full workspace setup.
