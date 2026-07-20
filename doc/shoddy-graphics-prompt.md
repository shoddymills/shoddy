# Shoddy Graphics — Implementation Prompt

## The name

In the West Riding shoddy and mungo trade, the fibre-preparation stage after
willeying and grinding was known as scribbling and carding, often on a combined
machine popularly nicknamed the "Devil." The scribbler is the upstream half of
that carding engine: it takes the oiled, blended fibre and begins the intensive
opening and mixing needed to make short recycled fibres spinnable.

A **scribbler** is Shoddy's drawing surface. Where other languages have a
canvas, Shoddy has the machine that does the first pass over raw stock — and
the individual dots it lays down are, by the same lineage, the fibres it
opens and mixes. (The lexer is already called the Devil, after the same
machine.)

---

## Context

Shoddy is a purely functional BASIC compiled onto a concatenative (Forth/Joy)
stack core, running on .NET 10. Every path is compiled: the front end lexes and
parses to a `ShoddyProgram`, the weaver generates C#, and Roslyn compiles it —
`mill run` just keeps the resulting assembly in memory.

```
src/
  Shoddy.Runtime/     — Engine.cs (stack VM + builtins), Model.cs (VType/Value/Node),
                        Format.cs (Format/Printer), Engine.Debug.cs (IDebugSink),
                        Machines.cs, ShoddyError.cs
  Shoddy.Devil/       — Lexer.cs, Parser.cs
  Shoddy.Compiler/    — Weaver.cs, CodeGen.cs, Machines.cs
  Shoddy.Mill/        — Program.cs (CLI), Dap.cs (DAP server)
  Shoddy.Tests/
machines/             — stdlib: seq, str, matrix, dict, file, isam, simplex, mps,
                        vt100, random, recio, money
```

The mill is the single CLI tool (`OutputType=Exe`, `AssemblyName=mill`, net10.0).
The runtime is a class library with no third-party dependencies. Debugging is
handled entirely by VS Code via `mill dap` (DAP over stdio) — already complete,
must not be changed.

Facts an implementer will need and should not re-derive from memory:

- `Value` (`Model.cs`) is a **sealed class**, not a struct, with static `Of*`
  factories. `VType` is `Num, Str, Bool, Quot, Rec, Arr`.
- `Program.cs` uses **top-level statements** — there is no `static int Main`
  and no `RunShoddy` helper. `mill dap` is dispatched by an early
  `if (args.Length == 1 && args[0] == "dap") return DapServer.Serve();`.
- New builtins are `case "WORD":` arms in `Engine.Builtin()` **and** entries in
  the static `Engine.BuiltinWords` `HashSet<string>`. `CodeGen` consults
  `BuiltinWords` at weave time; a word missing from the set is an unknown word.
- Shoddy folds every name to uppercase for matching, keeping the as-declared
  spelling only for display. `CodeGen.EmitWord` resolves a name in this order:
  **local → global → def → external def → type → builtin → record field**.
  Two consequences that bite when adding words: a `Def` silently shadows a
  builtin of the same name, and a **builtin shadows a record field accessor of
  the same name**. Check every new builtin against existing type and field
  names, and every new field name against `BuiltinWords`.
- `Engine.PendingSink` is the existing precedent for a static seam between the
  runtime and the mill. `ScribblerRegistry` below follows it.
- Development happens on both Windows and macOS (`build.cmd`, `build.sh`).

Naming conventions in this document follow the codebase: C# types in
PascalCase (`ScribblerHandle`), Shoddy surface words in PascalCase
(`ScribblerOpen`, `DrawLine`), Shoddy parameters and locals in camelCase
(`sc`, `radius`, `keyChar`), and folded builtin names in uppercase
(`SCRIBBLEROPEN`) wherever the engine's switch and word set are meant.

---

## Task: add an interactive pixel scribbler to the mill

There is no separate Studio project. The mill gains a lightweight window/GL
stack (Silk.NET) and a scribbler window that opens when a Shoddy program calls
`ScribblerOpen`. One binary, one tool. A small, window-independent timing layer
comes with it, because animation and frame pacing are impossible without a
clock and Shoddy currently has none.

### Design principles

- **One mill** — `mill run FILE.shoddy` works as today for console programs.
  Scribbler programs get a window; other programs are unaffected.
- **Console is an output channel, not an input one.** `Print` keeps working
  while a window is up and is the natural place for logs. `Input` is
  **refused** while a window is open — see Input ownership below.
- **Debugging unchanged** — `Dap.cs` is untouched apart from rendering the new
  value type.
- **No Silk.NET in `Shoddy.Runtime`.** All drawing is CPU writes into a plain
  `byte[]` owned by the runtime. The mill only ever *reads* that buffer to blit
  it; it never writes pixel content. `ScribblerText` therefore lives entirely
  in the runtime — an embedded bitmap font is a byte table and some loops, so
  there is no rasterizer dependency to avoid in the first place.
- **Nothing spins.** A program waiting for input or for the next frame must sit
  at zero CPU. This is a hard requirement, not an optimisation; see Waiting.
- **Keep the non-scribbler path cheap.** GLFW init via `Silk.NET.Windowing` is
  near-instant — no XAML, no control tree, no reflection-heavy startup — so
  always starting the main-thread loop is acceptable. It must not, however,
  busy-spin for the whole life of a plain console program.

---

## Threading model

GLFW requires its event loop — and on macOS, window creation itself — to run on
the process's **main thread**. So:

- The Shoddy program runs on a **background thread**, as the DAP path already
  does (`Dap.cs` uses `Task.Run`).
- The main thread owns every window, every GL context, and the frame timer. It
  alternates draining a dispatcher queue and pumping windows, and exits once
  the program has finished **and** no windows remain open.
- `mill dap` returns before any of this — the DAP server keeps its current
  shape and never starts a window loop.

Constraints the implementation must satisfy:

1. **Window and GL-context creation, all GL calls, event pumping, and interval
   timing happen on the main thread.** Nothing else may touch GL, and the
   frame timer is a check inside the pump — not a `System.Threading.Timer`,
   which would put a second thread near the window.
2. `ScribblerRegistry.CreateScribbler` is called from the Shoddy thread and
   must **block** until the window exists — `ScribblerOpen` returns a handle
   the program draws into on the very next word. A post-and-forget queue is
   wrong here; a blocking marshal is right.
3. The program's exit code and completion flag cross a thread boundary and need
   real publication (`volatile`, a `ManualResetEventSlim`, or a `Task` result)
   — not a plain `bool` field.
4. When no window is open, the main thread must **block** on program
   completion rather than poll. A `Thread.Sleep(1)` spin for the entire
   duration of every `mill lex` and every console program is the one cost this
   design is not allowed to have.
5. With more than one window open, each window's GL context must be made
   current before its texture upload and draw. Forgetting this is the classic
   multi-window bug and it fails silently with one window.

**`Main` may return while a window is open.** The process stays alive until the
user closes it. This is deliberate: "open a scribbler, draw a picture, return"
is a complete program with no event loop at all, and it is the right shape for
a first lesson.

No `[STAThread]` — that is a COM/WinForms/WPF artifact, not a GLFW
requirement.

---

## The runtime/mill seam

These declarations are the contract between `Shoddy.Runtime` (which must not
reference Silk.NET) and `Shoddy.Mill` (which owns the window). Their shape is
fixed; everything else in this document is a requirement, not a signature.

```csharp
// Shoddy.Runtime — no Silk.NET, no System.Drawing, nothing but BCL.
public sealed class ScribblerHandle
{
    public byte[] Pixels = Array.Empty<byte>();   // RGBA, row-major, top-down
    public int Width, Height;
    public string Title = "";

    public readonly ConcurrentQueue<ScribblerEvent> Events = new();
    // Released by the window whenever it enqueues, and once on teardown.
    // SCRIBBLERWAIT blocks on this; nothing else may.
    public readonly SemaphoreSlim Signal = new(0);

    // Set by the mill's rendering layer when a window backs this handle;
    // null when headless.
    public Action<byte[], int, int>? OnBlit;
    public Action? OnClose;
    public Action<string>? OnSetTitle;
    public Action<int>? OnSetInterval;            // (milliseconds, 0 = off)

    // Every pixel write in the runtime goes through here — SCRIBBLERPIXEL,
    // SCRIBBLERFILL and Font8x8 all share it. Out-of-bounds writes are
    // dropped silently.
    public void SetPixelClamped(int x, int y, byte r, byte g, byte b) { ... }
}

public sealed class ScribblerEvent
{
    public enum Kind { None, MouseDown, MouseMove, MouseUp,
                       KeyDown, KeyUp, Typed, Tick, Quit }

    [Flags] public enum Mod { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Super = 8 }

    public Kind Type;
    public int X, Y, Button;     // Button: 1=left 2=right 3=middle
    public int Key;              // KeyDown/KeyUp: a ScribblerKeys code (below)
    public string KeyChar = "";  // Typed: the character; "" for every other kind
    public Mod Mods;
    public double At;            // TICKS value when the event was generated
}

public static class ScribblerRegistry
{
    // Set once by the mill at startup. Null when no window backend is running
    // (mill dap, mill weave output, mill machine).
    public static Func<int, int, ScribblerHandle>? CreateScribbler;

    // Incremented by SCRIBBLEROPEN, decremented by SCRIBBLERCLOSE and by
    // window teardown. Read by the INPUT builtin — see Input ownership.
    public static int OpenCount;
}
```

`Kind.None` is deliberately a member of the enum: it is the value
`ScribblerPoll` reports for an empty queue. Shoddy has **no null and no
Nothing** — do not invent one.

### Key codes

Do **not** expose Silk.NET's `Key` enum through the seam. Its values are
meaningless in Shoddy, undocumented, and would change silently if the
windowing library ever did. Define Shoddy's own small table in
`Shoddy.Runtime` — a `ScribblerKeys` static class of named `const int` — and
have `ScribblerWindow` map Silk.NET onto it. Assign stable numbers and never
renumber them.

Cover at minimum: letters and digits; `Enter`, `Escape`, `Backspace`, `Tab`,
`Space`, `Delete`, `Insert`; the four arrows; `Home`, `End`, `PageUp`,
`PageDown`; `F1`–`F12`; and the modifier keys themselves. Anything unmapped
reports 0.

`Key` identifies a **physical key**; `KeyChar` carries a **character**. They
are never derived from one another. The physical key is what hotkeys and
navigation want; the character is what text entry wants, and it must come from
Silk.NET's `IKeyboard.KeyChar` event so that keyboard layout, shift, and IME
are all handled by the platform rather than guessed at. That is why `Typed` is
its own event kind: `KeyDown` reports "the D key went down", `Typed` reports
"the user produced a `d`", and no code has to conflate them.

---

## Runtime changes

### `Model.cs`

Add `Scribbler` to `VType` and a `ScribblerHandle? Scribbler` field to `Value`,
with a `Value.OfScribbler` factory alongside the existing ones. Extend
`Value.TypeName` with `VType.Scribbler => "SCRIBBLER"`.

`Scribbler` is an **opaque, mutable, reference** value type — the one place
Shoddy departs from pure functional values. Equality (`Engine.EqualValues`) is
reference identity. It has no fields reachable from Shoddy, which is why
`ScribblerWidth`/`ScribblerHeight`/`ScribblerGetPixel` exist as builtins.

### `Format.cs`

`Printer.Repr` switches on `VType` with **no default arm**, so an unhandled
type prints as the empty string. Add a `Scribbler` case — something like
`Scribbler(640, 480)` — so `Print(sc)` is not silently blank.

### `Font8x8.cs` (new)

An embedded 8×8 monospace glyph renderer covering three codepoint ranges,
dispatched by `DrawChar`, with a `DrawText` that advances a fixed `8 * scale`
per character (monospace, no kerning). It writes through
`ScribblerHandle.SetPixelClamped` and has no dependencies.

- **32–126** — printable ASCII, from a baked-in table of 95 glyphs × 8 rows,
  one byte per row, MSB = leftmost pixel. Use Daniel Hepper's `font8x8`
  (public domain, github.com/dhepper/font8x8) — pin this specific source and
  credit it in the file header; do not substitute "some public-domain font"
  found at implementation time, and note that its rows are LSB-first, so it
  needs a bit reversal or an adjusted inner loop.
- **128–191** — TRS-80 semigraphics. Computed, not looked up: the low 6 bits
  of the code say directly which of six sub-blocks are filled, in the TRS-80's
  own order (0=top-left, 1=top-right, 2=mid-left, 3=mid-right, 4=bottom-left,
  5=bottom-right). Split the 8×8 cell 4/4 across and 3/3/2 down.
- **U+2500–U+253C, light single-line only** (`─│┌┐└┘├┤┬┴┼`, 11 glyphs).
  Also computed: each glyph is which of up/down/left/right arms reach the
  centre, drawn as 1px-wide (times scale) segments from the centre row/column.
  The rest of the box-drawing block — double, heavy, dashed, arcs — renders
  blank. A real gap if double-line borders matter; it covers the common case.

Anything else renders blank. `scale` is an integer pixel multiplier
(1 = 8×8 px/char, 2 = 16×16), nearest-neighbour — not a point size.

---

## Timing builtins (no window required)

Three words that have nothing to do with scribblers and work everywhere —
headless, under `mill dap`, and in woven output. They exist because pacing an
animation is impossible without a clock, and `Engine.BuiltinWords` currently
has no time-related word at all.

```
TICKS  ( -- n )     Monotonic milliseconds since program start, fractional.
                    Stopwatch-based. Never wall-clock, never runs backwards.
SLEEP  ( ms -- )    Yield the calling thread. Zero-result, like PRINT.
CLOCK  ( -- arr )   Wall clock as a 7-element Array:
                    [year, month, day, hour, minute, second, ms]
```

`Ticks` and `Clock` are deliberately separate. `Ticks` is for **measuring** —
frame pacing, deltas, benchmarks — and is the only one animation may use.
`Clock` is for **stamping** — logs, filenames — and will jump whenever the OS
adjusts system time. Keeping them apart now avoids the classic bug where an
animation is paced off wall-clock and hiccups twice a year.

`Clock` returns a flat Array for the same reason `ScribblerPoll` does: a
builtin cannot reach a `TypeDef`. Formatting is the machine's job.

`Sleep` on the Shoddy thread is safe while a window is open — the main thread
pumps independently, so the window keeps repainting and queueing events.

Two cautions: golden tests must never assert on `Ticks` or `Clock` output, and
`machines/clock.shoddy` must not define a `Def` named `Ticks`, `Sleep`, or
`Clock`, because a `Def` silently shadows a builtin.

---

## Scribbler builtins

Thirteen words, added to both `Engine.Builtin()` and `Engine.BuiltinWords`.

**Every one of these leaves exactly one value** (except where marked). This is
not a style preference. A surface call `F(a, b)` compiles to "push the
arguments, run the word" with no arity check (`CodeGen.EmitWord`), so a word
that leaves two values strands one on the stack at every call site — and
because self tail calls compile to a `continue` loop rather than a call, an
event loop would grow the stack without bound. Returning the scribbler from
every mutator is what makes `Let sc = ScribblerFill(sc, 0, 0, 0)` read
functionally even though the handle is mutated in place.

```
SCRIBBLEROPEN      ( width height -- scribbler )
    Calls ScribblerRegistry.CreateScribbler. If it is null, raise a
    ShoddyError ("ScribblerOpen: no window backend — scribbler programs
    require `mill run`"). Failing loudly is deliberate; see Headless paths.
    Increments ScribblerRegistry.OpenCount.

SCRIBBLERPIXEL     ( scribbler x y r g b -- scribbler )   one RGBA write, clamped
SCRIBBLERFILL      ( scribbler r g b -- scribbler )       fill the whole buffer
SCRIBBLERTEXT      ( scribbler x y scale r g b text -- scribbler )
                       Font8x8.DrawText into the buffer
SCRIBBLERGETPIXEL  ( scribbler x y -- arr )
                       3-element Array of r, g, b (alpha dropped).
                       Out-of-bounds reads yield 0, 0, 0.
SCRIBBLERWIDTH     ( scribbler -- n )
SCRIBBLERHEIGHT    ( scribbler -- n )
    The five above are pure buffer/field access. They work headless.

SCRIBBLERBLIT      ( scribbler -- scribbler )   OnBlit if non-null, else no-op
SCRIBBLERCLOSE     ( scribbler -- scribbler )   OnClose if non-null, else
                                                no-op; idempotent; decrements
                                                OpenCount exactly once
SCRIBBLERTITLE     ( scribbler title -- scribbler )
                       store Title, then OnSetTitle if non-null

SCRIBBLERPOLL      ( scribbler -- arr )
    Take one event if there is one and return immediately. kind 0 = empty.

SCRIBBLERWAIT      ( scribbler -- arr )
    Block until an event is available, then take it. Zero CPU while waiting.
    Never returns kind 0.

SCRIBBLERSETINTERVAL ( scribbler ms -- scribbler )
    Emit a Tick event every ms milliseconds; 0 disables. Ticks arrive on the
    same queue as input, so one Wait loop serves both.
```

Every event word returns the same 8-element Array:

```
  1 kind     Number, matching ScribblerEvent.Kind (0 = queue empty)
  2 x        Number
  3 y        Number
  4 button   Number
  5 key      Number — a ScribblerKeys code
  6 keyChar  String — Typed events only, "" otherwise
  7 mods     Number — bitfield: 1 Shift, 2 Ctrl, 4 Alt, 8 Super
  8 at       Number — TICKS value when the event was generated
```

An Array of mixed element types is fine; `Value[]` holds `Value`s. The
timestamp is on **every** kind, not just ticks: it is what lets a frame handler
compute a true delta without drift, and it is the only way to detect a
double-click or a drag velocity. Element 7 and element 8 are cheap now and
break every decoder ever written if they arrive later — which is why they are
in the shape from the start.

The event word is `ScribblerPoll`, not `ScribblerEvent`, because
`machines/scribbler.shoddy` declares a `ScribblerEvent` **type** and Shoddy
folds both to `SCRIBBLEREVENT` — they would be the same name.

`ScribblerPoll`/`ScribblerWait` return a flat Array, **not** a record, because
a builtin in `Shoddy.Runtime` has no way to reach a `TypeDef`: `Engine.Ctor` is
only ever called from woven code holding a static `TypeDef` field emitted by
`CodeGen`, and there is no runtime name→type registry. Decoding the array into
a proper sum type is the machine's job. Do not add a type registry to the
runtime for this.

### Waiting

`ScribblerWait` blocks on `handle.Signal`. Three things it must get right:

- The window releases `Signal` once per enqueue, and **once more on teardown**.
  Without the teardown release, closing the window leaves the Shoddy thread
  blocked forever and the process never exits.
- After a teardown wake with an empty queue, `Wait` returns a `Quit` event.
  A program's `Wait` loop must always be able to reach its exit branch.
- Headless, `Wait` raises a `ShoddyError` rather than blocking. There is
  nothing to wake it.

### Queue discipline

The queue is bounded and lossy by design, because a program that is busy
drawing is not polling:

- **Coalesce consecutive `MouseMove`s** — only the newest position matters.
  Without this, a minute of mouse waving leaves thousands of stale events to
  chew through.
- **At most one pending `Tick`.** If the handler cannot keep up, drop missed
  ticks rather than queueing them; otherwise one slow frame spirals into an
  ever-growing backlog. The program can see the drop in the `at` timestamp.
- **Cap the queue** (a few thousand) and drop oldest on overflow.

---

## Input ownership

The console and the scribbler are two separate input channels, and which one
receives a keystroke is decided by the OS focus, not by Shoddy. With the
scribbler window focused, keystrokes reach GLFW and appear as events; they
never reach `Console.In`. With the terminal focused, the reverse.

So a program that calls `Input(...)` while its window is focused blocks in
`ReadLine()` forever while the user types into a window that is queueing
keystrokes nobody reads. The window still repaints, so it looks alive but is
deaf.

**Make this impossible rather than documented.** The `INPUT` builtin raises a
`ShoddyError` when `ScribblerRegistry.OpenCount > 0`:

> `Input: cannot read the console while a scribbler window is open — read
> keystrokes with ScribblerWait or ScribblerPoll.`

`Input` before opening a window, or after closing one, still works. The only
case that changes is the one that silently hangs, and it becomes a message
naming the fix.

`Print` is unaffected and is the intended logging channel while scribbling —
nothing else writes to the console, so no locking is needed.

---

## Headless paths

`ScribblerRegistry.CreateScribbler` is null in three situations, and the
behaviour in each must be deliberate:

- **`mill dap`** — the DAP server never starts a window loop. A scribbler
  program under the debugger raises the `SCRIBBLEROPEN` error immediately. That
  is far better than the alternative: a windowless program whose event queue is
  permanently empty, blocked in `Wait` with no way out.
- **`mill weave` output** — `Weaver.Weave` emits a standalone assembly plus a
  `.runtimeconfig.json`, run as `dotnet FILE.dll` with no mill in the process.
  Scribbler programs woven this way will raise at `ScribblerOpen`. Document
  this as a known limitation in the machine's header comment: scribbler
  programs are `mill run` programs. (Lifting the restriction later means
  factoring the window layer into its own assembly copied beside the woven
  output — out of scope here, but do not design anything that forecloses it.)
- **`mill machine`** — compiling `scribbler.shoddy` itself never runs it, so
  this is a non-issue; it is listed only so the implementer does not go
  looking.

The five pure-buffer builtins and all three timing builtins work in every case,
which is what makes the drawing code testable without a window.

---

## Mill's rendering layer

Add to `Shoddy.Mill.csproj`: `Silk.NET.Windowing`, `Silk.NET.Input`,
`Silk.NET.OpenGL` (version `2.*`), plus whatever native GLFW package the
target platforms need. Nothing is added to `Shoddy.Runtime`.

OpenGL is used purely as a **presentation** mechanism: upload the software
pixel buffer as one texture, draw one fullscreen textured quad, done. Nothing
is GPU-rendered.

Three pieces are needed:

**A main-thread dispatcher** (`MainThreadDispatcher.cs`). A concurrent queue of
`Action` with a post, a blocking invoke, and a drain-on-main-thread. This is
the one bit of framework glue the design needs, replacing what a UI toolkit's
`Dispatcher.UIThread` gives for free. Keep it small; get the blocking invoke's
publication right.

**A window registry** (`ScribblerWindows.cs`). Tracks open windows so the main
loop knows when it is safe to exit, and pumps each one. Mutated only from the
main thread. Pumping must tolerate a window unregistering itself mid-iteration.

**A scribbler window** (`ScribblerWindow.cs`). Wraps a Silk.NET `IWindow` plus
an `IInputContext` from `CreateInput()`, and owns one texture sized to the
scribbler.

- Request an explicit **OpenGL 3.3 core, forward-compatible** profile in
  `WindowOptions`. macOS caps out at 4.1 core and refuses a compatibility
  context; the default profile will not do what you want there.
- `OnBlit` marks the window dirty; the actual `glTexSubImage2D` and redraw
  happen during the next main-thread pump, because GL calls are affine to the
  context and `OnBlit` runs on the Shoddy thread.
- `OnClose` tears down GL resources, closes the window, unregisters, decrements
  `OpenCount`, and releases `Signal`. Idempotent — it can arrive from
  `SCRIBBLERCLOSE` and from a user-initiated close.
- `OnSetInterval` records the period; the pump compares elapsed `Ticks` each
  iteration and enqueues at most one pending `Tick`.
- `input.Mice[0]` supplies mouse events; `input.Keyboards[0].KeyDown`/`KeyUp`
  supply physical keys mapped through `ScribblerKeys`; and
  `input.Keyboards[0].KeyChar` supplies `Typed` events. Modifier state comes
  from the keyboard's currently-pressed set at the moment the event is built.
  Decide explicitly whether GLFW auto-repeat passes through as further
  `KeyDown`s — a text field wants held-Backspace to repeat, so it probably
  should — and say which way in a comment.
- Every enqueue stamps `At` from the same monotonic source as the `TICKS`
  builtin and releases `Signal`.
- `window.Closing` pushes a `Quit` event and runs the same teardown.

This layer has nothing to do with text rendering. `ScribblerText` writes into
`handle.Pixels` via `Font8x8` entirely inside `Shoddy.Runtime`, exactly like
`ScribblerPixel`. The mill only ever blits what is already in the buffer.

**Pixel orientation.** `Pixels` is row-major top-down — `(x, y)` with `y`
increasing downward, matching `ScribblerPixel`'s screen-coordinate convention —
while GL texture space is bottom-up. Flip V in the quad's UVs or in the
fragment shader, or everything renders upside down.

---

## `machines/clock.shoddy`

The window-independent timing library, usable by any console program. The
builtins are callable without including anything; this machine is the derived
layer, the way `vt100.shoddy` is derived from `Chr`.

```basic
Def Elapsed(since As Number) As Number       Rem Ticks() - since
Def TimeIt(f As [ -- ]) As Number            Rem run f, return ms taken
Def SleepUntil(t As Number)                  Rem sleep the remainder to t
Def Stamp() As String                        Rem ISO-8601 from Clock()
Def StampDate() As String                    Rem YYYY-MM-DD
Def StampTime() As String                    Rem HH:MM:SS.mmm
Def FormatDuration(ms As Number) As String   Rem "1m 04.2s"
Def Pad2(n As Number) As String              Rem zero-padding helper
```

No `Def` here may be named `Ticks`, `Sleep`, or `Clock` — a `Def` shadows a
builtin silently.

---

## `machines/scribbler.shoddy`

Carries the standard `Rem` copyright header like every other machine, and
`Include "seq.shoddy"` plus `Include "clock.shoddy"` (the in-`machines/`
include convention is a bare filename). Compile with
`mill machine machines/scribbler.shoddy`.

The event type is a sum type in the style of `Vt100Key`. **Every constructor is
prefixed `Scribbler`** — `machines/vt100.shoddy` already exports `KeyUp` and
`KeyDown` as `Vt100Key` constructors, names fold to uppercase, and a program
including both machines must not collide. Note also that constructor **field**
names must avoid builtin names, since a builtin outranks a field accessor: the
tick's timestamp field is `At`, not `Ticks`.

```basic
Type ScribblerEvent = ScribblerNone
                    | ScribblerMouseDown(X, Y, Button, Mods, At)
                    | ScribblerMouseMove(X, Y, Mods, At)
                    | ScribblerMouseUp(X, Y, Button, Mods, At)
                    | ScribblerKeyDown(Key, Mods, At)
                    | ScribblerKeyUp(Key, Mods, At)
                    | ScribblerTyped(Ch, Mods, At)
                    | ScribblerTick(At)
                    | ScribblerQuit

Type Point
    X As Number
    Y As Number

Rem Decode the 8-element array from ScribblerPoll / ScribblerWait into the
Rem sum type above — Select Case on element 1, build the matching constructor.
Def NextEvent(sc As Scribbler) As ScribblerEvent     Rem blocking (Wait)
Def PeekEvent(sc As Scribbler) As ScribblerEvent     Rem non-blocking (Poll)

Rem ---- frame timing --------------------------------------------------
Def SetFps(sc As Scribbler, fps As Number) As Scribbler
    Rem ScribblerSetInterval(sc, Round(1000 / fps))
Def SinceLast(e As ScribblerEvent, prev As Number) As Number
    Rem the At field of e, minus prev — the frame delta, drift-free
```

Higher-level drawing, all implemented in Shoddy on top of `ScribblerPixel`:

```basic
Def DrawHLine(sc As Scribbler, y As Number, x0 As Number, x1 As Number,
              r As Number, g As Number, b As Number) As Scribbler

Def DrawVLine(sc As Scribbler, x As Number, y0 As Number, y1 As Number,
              r As Number, g As Number, b As Number) As Scribbler

Def DrawLine(sc As Scribbler, x0 As Number, y0 As Number,
             x1 As Number, y1 As Number,
             r As Number, g As Number, b As Number) As Scribbler
    Rem Bresenham — DrawHLine/DrawVLine alone cannot do diagonals

Def DrawRect(sc As Scribbler, x As Number, y As Number,
             w As Number, h As Number,
             r As Number, g As Number, b As Number) As Scribbler   Rem outline

Def FillRect(sc As Scribbler, x As Number, y As Number,
             w As Number, h As Number,
             r As Number, g As Number, b As Number) As Scribbler

Def DrawCircle(sc As Scribbler, cx As Number, cy As Number, radius As Number,
               r As Number, g As Number, b As Number) As Scribbler  Rem midpoint

Def FillCircle(sc As Scribbler, cx As Number, cy As Number, radius As Number,
               r As Number, g As Number, b As Number) As Scribbler

Def DrawPolyline(sc As Scribbler, points As List Of Point,
                 r As Number, g As Number, b As Number) As Scribbler
    Rem open path, no closing edge

Def DrawPolygon(sc As Scribbler, points As List Of Point,
                r As Number, g As Number, b As Number) As Scribbler
    Rem DrawPolyline plus the closing edge

Def FloodFill(sc As Scribbler, x As Number, y As Number,
              r As Number, g As Number, b As Number) As Scribbler
    Rem Read the seed colour with ScribblerGetPixel, then fill. Use a
    Rem scanline fill with an explicit worklist of spans — NOT four-way
    Rem recursion. Only *self* tail calls are optimised into a loop; a
    Rem mutually recursive or non-tail flood fill overflows the stack long
    Rem before it covers a 640x480 region.
```

`FloodFill` needs no auxiliary visited-set: `ScribblerGetPixel` and
`ScribblerPixel` read and write the same buffer the fill is walking, so a
filled pixel no longer matches the seed colour.

### Deliberately left for later

A keyboard-classification layer and a text-entry example are the intended next
increment, and are **out of scope here**. They are pure Shoddy on top of what
is specified above and need no runtime, seam, or mill changes:

- `Type Keystroke = Printable(Ch) | Enter | Backspace | Escape | Left | ...`
  with a `ClassifyKey` that folds `KeyDown` and `Typed` into one match, so
  programs never compare against a raw key code.
- A line-reader built as a pure transition function over that type — the
  natural teaching example, since Shoddy's tail-recursive loop carries its
  state as an argument rather than a mutable variable.

Nothing in this document should foreclose either. In particular, keep `Key`
codes stable and keep `Typed` separate from `KeyDown`.

---

## Example program (`tst/scribbler-demo.shoddy`)

Include path follows the `tst/` convention (`Include "../machines/..."`).
The loop blocks in `NextEvent`, so it sits at zero CPU between clicks — there
is no idle arm and nothing spins. Every branch of a `Select Case` must yield
the same arity, so the quit branches return the closed scribbler rather than
nothing.

```basic
Include "../machines/scribbler.shoddy"

Def Main()
    Let sc = ScribblerOpen(640, 480)
    Let sc = ScribblerTitle(sc, "Shoddy Scribbler Demo")
    Let sc = ScribblerFill(sc, 20, 20, 40)
    Let sc = ScribblerText(sc, 10, 10, 2, 255, 255, 255, "Click to paint, Q to quit")
    Print("scribbler opened " & Stamp())
    EventLoop(ScribblerBlit(sc))

Def EventLoop(sc As Scribbler) As Scribbler
    Select Case NextEvent(sc)
        Case ScribblerMouseDown(x, y, btn, mods, at)
            EventLoop(ScribblerBlit(FillRect(sc, x - 2, y - 2, 5, 5, 255, 200, 0)))
        Case ScribblerTyped(ch, mods, at)
            If Lower(ch) = "q" Then
                ScribblerClose(sc)
            Else
                EventLoop(sc)
        Case ScribblerQuit
            ScribblerClose(sc)
        Case Else
            EventLoop(sc)
```

Run with `mill run tst/scribbler-demo.shoddy`.

Two things to confirm while writing this: that the recursion is a genuine self
tail call, so `CodeGen` turns it into a loop rather than growing the CLR stack
one frame per event; and that `Main` leaving the returned scribbler on the
stack is harmless — if it is not, bind it with a `Let` and finish with a
`Print`.

Note the demo matches `ScribblerTyped`, not `ScribblerKeyDown` — "the user
typed a q" is a character question, not a physical-key one.

---

## Tests

The pure-buffer builtins and the timing builtins work headless, so most of this
is testable in `Shoddy.Tests` alongside the existing machine and golden tests,
with no window and no GL:

- `ScribblerFill` then `ScribblerGetPixel` round-trips a colour.
- Out-of-bounds `ScribblerPixel` writes and `ScribblerGetPixel` reads clamp
  rather than throw.
- `ScribblerText` renders a known glyph at a known offset — assert a handful of
  pixels, including one from each of the three codepoint ranges.
- `ScribblerWidth`/`ScribblerHeight` report what `ScribblerOpen` was given.
- `NextEvent`/`PeekEvent` decode a synthesised 8-element array into the right
  constructor, including `mods` and `At`.
- `Ticks` is monotonic across two reads; `Sleep(50)` advances it by at least
  50; `Clock` returns seven plausible fields.
- `machines/clock.shoddy` and `machines/scribbler.shoddy` compile cleanly with
  `mill machine`.
- `Input` raises while `OpenCount > 0` and works when it is 0.
- A woven program that never touches a scribbler produces byte-identical output
  to before the change.
- No golden test asserts on `Ticks` or `Clock` output.

Window creation, blitting, input delivery, and interval timing are the only
genuinely untestable parts; keep as little logic there as possible.

---

## Checklist

- [ ] `VType.Scribbler` + `ScribblerHandle? Scribbler` + `Value.OfScribbler` +
      `TypeName` in `Model.cs`; reference equality in `Engine.EqualValues`
- [ ] `ScribblerHandle` (with `SetPixelClamped` and `Signal`),
      `ScribblerEvent` (with `Mods` and `At`), `ScribblerKeys`,
      `ScribblerRegistry` (with `OpenCount`) in `Shoddy.Runtime` — no Silk.NET
- [ ] `Printer.Repr` in `Format.cs` gained a `Scribbler` arm (it has no default)
- [ ] `Font8x8.cs`: pinned public-domain ASCII table (bit order checked),
      computed semigraphics, computed light box-drawing, `DrawChar`/`DrawText`
- [ ] 3 timing builtins (`TICKS`, `SLEEP`, `CLOCK`) — no window required
- [ ] 13 scribbler builtins in `Engine.cs` **and** `Engine.BuiltinWords`
- [ ] Event array is 8 elements including `mods` and `at`; the timestamp is on
      every event kind, not just ticks
- [ ] `SCRIBBLERWAIT` blocks at zero CPU, is released on teardown, returns
      `Quit` rather than hanging, and raises headless
- [ ] `MouseMove` coalesced, at most one pending `Tick`, queue capped
- [ ] `INPUT` raises while `OpenCount > 0`, naming `ScribblerWait`/`Poll`
- [ ] No folded-name collisions: builtins vs `Type` names vs record **field**
      names (a builtin outranks a field accessor — hence `At`, not `Ticks`),
      and no constructor collides with `vt100.shoddy`'s `KeyUp`/`KeyDown`
- [ ] `Key` is a `ScribblerKeys` code, never a raw Silk.NET enum value;
      `KeyChar` comes from the `KeyChar` event as a `Typed` event, never
      derived from `Key`
- [ ] `SCRIBBLEROPEN` raises a clear `ShoddyError` when there is no backend —
      never silently headless
- [ ] `Shoddy.Mill.csproj` references the three Silk.NET packages
- [ ] `Program.cs`'s top-level statements run the program on a background
      thread and drive the main-thread loop; `mill dap` still returns before it
- [ ] Main thread blocks (not spins) when no window is open; cross-thread state
      is properly published; `Main` returning leaves an open window up
- [ ] Interval timing runs in the main-thread pump, not a separate timer thread
- [ ] Each window's GL context is made current before its upload and draw
- [ ] OpenGL 3.3 core forward-compatible requested; V flipped
- [ ] `Dap.cs` variable rendering handles a `Scribbler` value (it switches on
      `VType` in three places)
- [ ] `mill run`, `mill dap`, `mill weave`, `mill machine`, `mill lex` all
      unaffected for non-scribbler programs — including startup cost
- [ ] `machines/clock.shoddy` and `machines/scribbler.shoddy` compile
- [ ] `FloodFill` is scanline with an explicit worklist, not recursive
- [ ] `tst/scribbler-demo.shoddy` runs, paints on click, renders text, quits
      on Q, and idles at zero CPU
- [ ] Headless tests added and passing
