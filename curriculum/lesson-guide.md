<!--
  Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.

  This file is part of the Shoddy Language project.
  Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
  License 1.0.0 with Additional Use Grant). See the LICENSE file in the
  project root for full terms.
-->

# Lesson Setup Guide

How to stand up a VS Code working folder for an apprenticeship lesson and get
the Craftsman talking. Assumes VS Code and the Shoddy extension
(`vscode-shoddy`) are already installed. Two parts: **Part A** runs once per
machine; **Part B** repeats for every lesson folder.

## Why Part A matters: how `Include` finds `machines/`

A lesson folder should never need its own copy of `machines/`. The project's
own mechanism handles this: `Include "seq.shoddy"` (a bare filename) checks
the including file's own directory first, then falls back to the
`SHODDYLIB` environment variable's directory (`agent-context.md` §1, §12).
Set `SHODDYLIB` once to the repo's `machines/` folder and every lesson
folder — anywhere on disk — can `Include` the standard library with no
relative paths and no drift between copies.

The one thing to get right: the extension invokes `mill` five different
ways (Run, Weave, Machine, Show Generated C#, F5 Debug), and they don't all
inherit environment identically. Run/Weave/Machine go through VS Code's
integrated terminal (inherits `terminal.integrated.env.*` from workspace
settings); Show Generated C# and F5 Debug spawn the process directly from
the extension host (inherit **only** the OS-level environment VS Code itself
was launched with). Setting `SHODDYLIB` in a workspace's
`terminal.integrated.env` would make Run work while silently breaking F5
debugging on the first Include-using lesson. Setting it at the **OS/user
level** reaches all five paths, because that's what the VS Code process
inherits at launch (restart VS Code once after setting it).

## Part A — One-time machine setup

1. **Build the mill**, if `bin\mill.exe` doesn't exist yet:
   ```
   build.cmd build
   ```
   Verify:
   ```
   bin\mill.exe run tst\examples.shoddy
   ```
   should print `HELLO FROM SHODDY` and a run of worked examples.

2. **Set `SHODDYLIB`** (User environment variable, not just a workspace
   setting) to the repo's `machines/` folder:
   ```
   setx SHODDYLIB "C:\github\shoddy\machines"
   ```
   (or System Properties → Environment Variables → New, under *User
   variables*). **Restart VS Code** (and any open terminals) afterward —
   environment variables are snapshotted at process launch, so a running
   VS Code window won't see this until it restarts.

3. **Point the extension at the mill, at User scope** — not per-workspace —
   so every future lesson folder finds it with zero per-folder setup. Open
   `Ctrl+Shift+P` → *Preferences: Open User Settings (JSON)* and add:
   ```json
   "shoddy.millPath": "C:\\github\\shoddy\\bin\\mill.exe"
   ```

4. **Smoke-test the whole chain** from a scratch folder *outside* the repo
   (this is the real test — it proves a lesson folder anywhere on disk will
   work, not just files inside the source tree):
   - Create `C:\temp\shoddytest\check.shoddy`:
     ```
     Include "seq.shoddy"

     Def Main()
         Print(Sum({ 1, 2, 3 }))
     ```
   - Open the folder in VS Code, run it (▶ or `Ctrl+Shift+B`) — expect `6`,
     not `unknown word: Sum` or a "cannot open" error.
   - Press **F5** on the same file and confirm it also runs clean under the
     debugger — this is the step that catches the terminal-vs-extension-host
     environment gap described above, before it surprises you mid-lesson.
   - Delete the scratch folder once both pass.

Do this once. Re-do only if the repo moves or the mill is rebuilt at a new
path.

## Part B — Per-lesson working folder (repeat each lesson)

1. **Create a folder** for the lesson, e.g. `C:\apprentice\lesson-01\`. One
   folder per lesson keeps each apprentice attempt isolated and avoids stale
   files from a prior lesson confusing a fresh Craftsman session.

2. **Open it in VS Code**: `code C:\apprentice\lesson-01`. No
   `.vscode/settings.json` is needed here — the User-scoped `shoddy.millPath`
   from Part A already covers it. (Only add a workspace `.vscode/settings.json`
   if this particular folder needs to override the mill path or add
   `terminal.integrated.env` entries for some other reason.)

3. **Leave the file empty for now** — the lesson's own Step 2 (the
   Challenge) tells the apprentice what filename to create and what to write;
   don't pre-create it or the Craftsman's ritual is short-circuited.

4. **Start a chat** with your model provider and paste that lesson's
   instructions prompt (§1 of `curriculum/lesson-0N.md`) into the
   system-instruction slot.
   > As of this repo's current state, only **Lesson 1** (`lesson-01.md`) has
   > a written instructions prompt. Lessons 2–7 exist as built student pages
   > (`lesson-0N.html`) but have no authored Craftsman prompt yet — that's
   > the open item tracked separately; don't paste an `.html` file in as if
   > it were the prompt.

5. **Attach `curriculum/agent-context.md`** as background reference in the
   same chat if your provider supports file upload. For Lesson 1 this is
   optional — its `<shoddy_facts>` block is already self-contained. From the
   Include-using lessons onward (and for anything touching records, sum
   types, or the `machines/` library — currently unscheduled but real, see
   `agent-context.md` §17) it's the reference that keeps the Craftsman
   accurate without widening what it's allowed to *teach* — the lesson's own
   `<shoddy_facts>` still governs scope, per `agent-context.md` §0.

6. **Let the Craftsman open** — its `<opening>` block fires the first turn
   automatically; no prompt needed from you beyond sending the system
   instructions.

7. **Work the lesson** in the open folder: apprentice writes, runs (▶ /
   `Ctrl+Shift+B`), predicts, and — once the perch is relevant — debugs (F5),
   exactly as `agent-context.md` §1 and `doc/VSCODE.md` describe.

## Apprentice-authored Include files

Once a lesson moves past a single file, the apprentice doesn't need
`SHODDYLIB` or any setup at all to split their own program in two —
`Include` checks the including file's own folder *first*, before ever
consulting `SHODDYLIB`. So a sibling file just works:

```
C:\apprentice\lesson-05\
    main.shoddy       ' Include "helpers.shoddy"
    helpers.shoddy
```

and a personal subfolder works the same way this repo's own `tst/` reaches
`machines/` (`Include "../machines/matrix.shoddy"`):

```
C:\apprentice\lesson-15\
    project.shoddy     ' Include "lib/vectors.shoddy"
    lib\
        vectors.shoddy
```

Two things to tell an apprentice once they reach this point (see
`agent-context.md` §3 for the underlying rule): don't name a personal file
the same as a real `machines/*.shoddy` file (`str.shoddy`, `seq.shoddy`,
…) — the local one silently wins with no warning, so `Split` or `Sum`
would quietly stop being the standard library's version. And there's no
way to add a *second* `SHODDYLIB`-style fallback directory — only relative
paths let them reuse a personal file across more than one lesson folder.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `unknown word: Sum` (or any library word) on Run, but F5 Debug also fails the same way | `SHODDYLIB` not set, or VS Code wasn't restarted after `setx` |
| Works on ▶ / Ctrl+Shift+B but fails only on F5 or "Show Generated C#" | `SHODDYLIB` was set only in a workspace's `terminal.integrated.env`, not at the OS/user level — see the note above |
| "Shoddy: no saved .shoddy file is active" | Command run without a `.shoddy` file focused/saved — save the file first |
| Mill not found at all | `shoddy.millPath` missing or wrong; check it's in *User* settings, not lost in a workspace override |
| A lesson folder needs a *different* mill build or lib path than usual | Add a workspace-level `.vscode/settings.json` for just that folder — it overrides the User setting |
