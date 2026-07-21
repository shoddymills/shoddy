# Shoddy in Visual Studio Code

*Making useless things useful, through skill — now with a Run button.*

This guide sets up VS Code to write, run, weave, and test Shoddy with
the mill. It assumes the repository is checked out and nothing else.

> **Platform note.** Commands and key bindings below are written for
> macOS (`Cmd`, zsh, `brew`). On Windows and Linux the flow is
> identical — substitute `Ctrl` for `Cmd`, use `./build.ps1` instead of
> `./build.sh` on Windows, and set environment variables the usual way
> for your shell.

## 1. Prerequisites

- **.NET SDK 10** — check with `dotnet --version`. Install from
  <https://dotnet.microsoft.com/download> or `brew install dotnet-sdk`.
- **The mill** — not committed; build it from source. From the repo
  root:

  ```sh
  ./build.sh build          # Windows: ./build.ps1 build
  ```

  which runs `dotnet publish src/Shoddy.Mill -c Release -o bin` (a
  folder publish, not single-file: the weaver needs `Shoddy.Runtime.dll`
  on disk to reference from generated code). The executable lands at
  `bin/mill` (`bin/mill.exe` on Windows).

Sanity check from the repo root:

```sh
bin/mill run tst/examples.shoddy     # tour of the language
bin/mill run tst/libtest.shoddy      # ends: ALL ASSERTIONS PASSED
```

## 2. Put `mill` on your PATH (optional but pleasant)

Add to `~/.zshrc`:

```sh
export PATH="$HOME/path/to/shoddy/bin:$PATH"
export SHODDYLIB="$HOME/path/to/shoddy/machines"
```

`SHODDYLIB` is the fallback directory for `Include` — with it set,
`Include "seq.shoddy"` works from any directory, so you can write Shoddy
programs outside the repo.

## 3. Open the folder

```sh
code /path/to/shoddy
```

If `code` isn't on your PATH: in VS Code press `Cmd+Shift+P` and run
**Shell Command: Install 'code' command in PATH** once.

## 4. Install the Shoddy extension

The repo ships its own extension in [`vscode-shoddy/`](../vscode-shoddy/):
real Shoddy highlighting (keywords, builtins, quotations, stack-effect
signatures, `Rem`/`'` comments, all case-insensitive), block-aware
indentation, snippets (`def`, `select`, `fn`, ...), **native debugging
with `F5`** (section 6), and mill commands — **Shoddy: Run File**
(`ctrl+r`, plus the ▶ button), **Weave File**, **Build Machine from
File**, and **Show Generated C#**.

The extension is plain JavaScript — no build step. A packaged
`vscode-shoddy-0.9.1.vsix` ships in the folder; install it from the repo
root (works on macOS, Windows, and Linux):

```sh
code --install-extension vscode-shoddy/vscode-shoddy-0.9.1.vsix
```

Or in VS Code: Extensions view → `···` menu → **Install from VSIX…** →
pick the file. Reload when prompted.

If you've edited the extension, repackage first (the committed `.vsix`
may be stale):

```sh
npm install -g @vscode/vsce
cd vscode-shoddy && vsce package        # emits a fresh .vsix
```

To iterate on the extension without packaging, open the
`vscode-shoddy/` folder in VS Code and press **F5** — that launches an
Extension Development Host with it loaded.

Then reload VS Code (`Cmd+Shift+P` → **Developer: Reload Window**).
The extension finds the mill via the `shoddy.millPath` setting, falling
back to the workspace's `bin/mill` (`bin/mill.exe` on Windows), then
`mill` on your PATH.

The repo's `.vscode/settings.json` pins what matters either way:
Shoddy's blocks are indentation-based (a tab counts as 4) and the whole
corpus uses 4 spaces — let the editor keep it that way.

## 5. Run, weave, test — the tasks

The repo ships [`.vscode/tasks.json`](../.vscode/tasks.json) with six
tasks wired to the mill:

| Task | What it does |
|------|--------------|
| **Shoddy: run current file** | `mill run` on the file you're editing — bound to `Cmd+Shift+B` |
| Shoddy: weave current file | compiles to a `.dll` beside the source (`dotnet FILE.dll` runs it) |
| Shoddy: build machine from current file | compiles a `machines/*.shoddy` to `Shoddy.Machines.<Name>.dll` |
| Shoddy: show generated C# for current file | prints what the weave emits — the readable compiler output |
| Shoddy: golden conformance tests | `dotnet test` — the five goldens, the machines path, and the TCO check |
| Shoddy: rebuild mill into bin/ | re-publishes the mill into `bin/` (folder) |

Workflow: open `tst/examples.shoddy`, press **`Cmd+Shift+B`**, and the
program runs in the integrated terminal. Everything else is under
**Terminal → Run Task…** (`Cmd+Shift+P` → "Run Task").

For a dedicated run key, add to your `keybindings.json`
(`Cmd+Shift+P` → "Open Keyboard Shortcuts (JSON)"):

```json
{
    "key": "cmd+r cmd+r",
    "command": "workbench.action.tasks.runTask",
    "args": "Shoddy: run current file",
    "when": "editorLangId == shoddy"
}
```

## 6. Debugging — the perch

Shoddy has native debugging (in mill-speak, the **perch** — where woven
cloth was inspected for flaws). Open any `.shoddy` file and press
**`F5`**: no `launch.json` needed. The program compiles with debug
instrumentation and stops on its first line. Then:

- **Breakpoints** — click in the gutter of any `.shoddy` file,
  including the machines' sources (the perch always splices sources,
  so you can step straight into `Sum` or `MatMul`).
- **Stepping** — step over (`F10`), into (`F11`), out (`Shift+F11`).
  Stepping is source-line granular; a tail-recursive def loops in
  place without growing the stack, exactly as it executes.
- **Call Stack** — your defs by name: `UPDATEBINV ← SOLVE ← MAIN`.
- **Variables** — three scopes: **Locals** (the current def's bindings,
  shown as Shoddy values — records print as
  `Person(Name = "ANN", Age = 34)` and expand into fields), **Globals**
  (top-level Lets), and the **Value Stack** — half the story in a
  concatenative language.
- **Debug Console** — type a binding's name to inspect it; hovering a
  name in the editor shows its value.
- **Scribbler programs** — `ScribblerOpen` works under the perch: the
  window stays live (pumping, redrawing) while you sit at a
  breakpoint, and a program that returns with its window still open
  keeps the session alive until you close it — the same "draw a
  picture, return" semantics as `mill run`.

Limitation: `Input` reads end-of-file under the perch (interactive
stdin isn't wired through the debug session) — debug the logic, run
the interactive session with `Cmd+Shift+B`.

## 7. Interactive programs

Tasks run in the integrated terminal, so programs that use `Input`
just work — run `tst/gradebook.shoddy` with `Cmd+Shift+B` and type at the
prompts. To replay its scripted session instead:

```sh
bin/mill run tst/gradebook.shoddy < tst/golden/gradebook.in
```

## 8. Includes and machines

- `Include "FILE.shoddy"` resolves relative to the *including file*, then
  `$SHODDYLIB`. Include-once: a file arrives at most once.
- Everything is compiled: `mill run` weaves to memory and executes;
  `mill weave` writes the assembly to disk. Both link against a
  precompiled **machine** (`Shoddy.Machines.<Name>.dll` beside the
  `.shoddy`) when one exists, and splice the source otherwise. Build
  machines in dependency order (`seq` and `str` before the machines
  that include them):

  ```sh
  for m in seq str matrix money file recio dict isam simplex mps; do
      bin/mill machine machines/$m.shoddy
  done
  ```

- Curious what your program compiles to? **Shoddy: show generated C#**
  (or `bin/mill gen file.shoddy`). Self-tail-recursive defs show
  `continue;  // self tail call — the loop is the TCO`.

## 9. Errors and testing

Runtime and parse errors print `ERROR (line N): message` to the
terminal and exit 1 — the line number is within the file that defines
the failing code. `Assert(cond, "MSG")` is built in.

The conformance rule of the whole project: `tst/golden/` is the
constitution. After touching anything under `src/`, run the golden
tests task (or `dotnet test src/Shoddy.Tests`) — the five goldens
compiled and run in-process, the machines path, and the TCO check,
all byte-identical. Green means the mill is honest.

## 10. Quick reference

```sh
mill run x.shoddy        # compile in memory and run (default: mill x.shoddy)
mill weave x.shoddy      # compile -> x.dll (+ runtimeconfig + runtime dlls)
dotnet x.dll         # run the woven program
mill machine lib.shoddy  # compile a library to a machine DLL
mill gen x.shoddy        # print the generated C#
mill lex x.shoddy        # dump token lines (debug)
mill dap                 # DAP server — the perch (the editor launches this)
```

Docs live in `doc/`: `GUIDE.html` (start here), `SPEC.html`,
`QUICKREF.html`. The language itself is documented there; this file is
only the loom-side of the workshop.
