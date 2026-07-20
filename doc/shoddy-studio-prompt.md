# Shoddy Studio — Implementation Prompt

## Context

The Shoddy language lives at `C:\github\shoddy`. It is a purely functional BASIC
compiled onto a concatenative (Forth/Joy) stack core, running on .NET 10.

Key project structure:

```
src/
  Shoddy.Runtime/     — Engine.cs (stack VM + builtins), Model.cs (VType/Value/Node),
                        Engine.Debug.cs (IDebugSink, DebugFrame), Machines.cs, Format.cs
  Shoddy.Devil/       — lexer
  Shoddy.Compiler/    — Weaver.cs, CodeGen.cs, Machines.cs
  Shoddy.Mill/        — Program.cs (CLI: run/weave/machine/lex/gen/dap), Dap.cs (DAP server)
  Shoddy.Tests/
machines/             — stdlib: seq, str, matrix, dict, file, isam, simplex, mps,
                        vt100, random, recio, money
```

The mill is the single CLI tool (`OutputType=Exe`, net10.0). The runtime is a class
library. Debugging is handled entirely by VS Code via `mill dap` (DAP over stdio) —
already complete, must not be changed.

Value types (`VType` enum in `Model.cs`): `Num, Str, Bool, Quot, Rec, Arr`.
New builtins are added as `case "WORD":` entries in `Engine.Builtin()` and registered
in the static `Engine.BuiltinWords` `HashSet<string>`.

---

## Task: Add an interactive pixel canvas to the mill

There is no separate Studio project. The mill gains a lightweight window/GL
stack (Silk.NET) and a canvas window that opens automatically when a Shoddy
program calls `CanvasOpen`. One binary, one tool.

### Design principles

- **One mill** — `mill run FILE.shoddy` works as today for console programs.
  Canvas programs get a window automatically; non-canvas programs are unaffected.
- **Console I/O unchanged** — `Print`, `Input`, `vt100.shoddy` continue to use
  the console. Pass `Console.Out` and `Console.In` to the Engine as always.
  `OutputType` stays `Exe` so the console window is always present.
- **Debugging unchanged** — `mill dap` is untouched. Canvas builtins must no-op
  gracefully when called via `mill dap` (no window/GL loop running).
- **Do not change** `Shoddy.Runtime`, `Dap.cs`, or any existing source files
  beyond `Engine.cs`, `Model.cs`, and `Engine.BuiltinWords` in `Shoddy.Runtime`,
  plus `Program.cs` and `Shoddy.Mill.csproj` in `Shoddy.Mill`. One new file,
  `Font8x8.cs`, is added to `Shoddy.Runtime` (see below).
- **Do not add Silk.NET** as a dependency to `Shoddy.Runtime` — keep it pure
  .NET. `CANVASTEXT` uses an embedded bitmap font instead of a rasterizer
  library, so it has no dependency to avoid in the first place — it's just
  a byte table and loops, same as any other builtin.
- **Keep the non-canvas path cheap** — unlike a full UI framework, GLFW init
  (via `Silk.NET.Windowing`) is near-instant: no XAML, no styling, no control
  tree, no reflection-heavy startup. It's fine to always start the main-thread
  loop in `Main`; the tax for `mill lex`/`mill weave`/plain console programs
  is negligible.

---

## Threading model

GLFW (which `Silk.NET.Windowing` sits on) requires its event loop — and on
some platforms, window creation itself — to run on the process's main thread.
The Shoddy program runs on a background thread, same as before; the main
thread now runs a small hand-rolled loop instead of a UI framework's
`AppBuilder`.

`Program.cs` `Main` becomes:

```csharp
static int Main(string[] args)
{
    // mill dap: no window backend, just run the DAP server and exit
    if (args.Length == 1 && args[0] == "dap")
        return DapServer.Serve();

    int rc = 0;
    bool programDone = false;
    var programThread = new Thread(() =>
    {
        rc = RunShoddy(args);
        programDone = true;
    });
    programThread.IsBackground = true;
    programThread.Start();

    // Main thread: owns every open canvas window and its GL context.
    // MainThreadDispatcher lets CanvasRegistry.CreateCanvas (called from
    // the Shoddy thread) hop onto this thread to actually create a window.
    while (!programDone || CanvasWindows.AnyOpen)
    {
        MainThreadDispatcher.DrainOnMainThread();
        CanvasWindows.PumpAll();     // events + redraw for every open window
        Thread.Sleep(1);             // avoid pegging a core while idle
    }

    return rc;
}
```

No `[STAThread]` needed — that's a COM/WinForms/WPF artifact, not something
GLFW requires.

`CanvasRegistry.CreateCanvas` is set once, early in `Main` (or a static
constructor), to a closure that builds the `CanvasHandle` and marshals the
actual window creation onto the main thread:

```csharp
CanvasRegistry.CreateCanvas = (w, h) =>
{
    var handle = new CanvasHandle { Pixels = new byte[w * h * 4], Width = w, Height = h };
    MainThreadDispatcher.Invoke(() =>
    {
        var win = new CanvasWindow(handle);   // creates the IWindow + GL context — must run here
        CanvasWindows.Register(win);
    });
    return handle;
};
```

Note this uses `Invoke` (blocking), not `Post` — `CANVASOPEN` must return a
handle the Shoddy program can draw into immediately, not one that's still
being constructed on another thread.

---

## New runtime additions (in `Shoddy.Runtime`)

### 1. `VType.Canvas` — add to `Model.cs`

Add `Canvas` to the `VType` enum. Add a `CanvasHandle? Canvas` field to the
`Value` struct.

`CanvasHandle` — pure .NET, no Silk.NET reference:

```csharp
public sealed class CanvasHandle
{
    public byte[] Pixels;          // RGBA, row-major
    public int Width, Height;
    public string Title = "";
    public readonly ConcurrentQueue<CanvasEvent> Events = new();

    // Set by the mill's rendering layer before Execute; null = no-op
    public Action<byte[], int, int>? OnBlit;   // (pixels, w, h)
    public Action? OnClose;
    public Action<string>? OnSetTitle;         // (title)
}

public sealed class CanvasEvent
{
    public enum Kind { MouseDown, MouseMove, MouseUp, KeyDown, KeyUp, Quit }
    public Kind Type;
    public int X, Y, Button;
    public int Key;            // KeyDown/KeyUp only: Silk.NET Key enum cast to int
    public string KeyChar = "";  // KeyDown/KeyUp only: printable char if any, else ""
}
```

### 2. `CanvasRegistry` — add to `Shoddy.Runtime`

```csharp
public static class CanvasRegistry
{
    // Mill's rendering layer sets this before calling Execute.
    // Null when running headless (mill dap, mill weave, etc.)
    public static Func<int, int, CanvasHandle>? CreateCanvas;
}
```

### 3. `Font8x8.cs` — add to `Shoddy.Runtime`

An embedded 8x8 monospace glyph renderer covering three separate character
ranges, dispatched by codepoint. No asset file, no dependency for any of
them — bitmap lookup for one, pure computation for the other two:

- **32–126** — printable ASCII, from a baked-in 8x8 bitmap table (95 glyphs).
- **128–191** — TRS-80 semigraphics: a 2x3 block mosaic where the low 6 bits
  of the code directly say which of the six sub-blocks are filled. This is
  computed, not looked up — it's an exact reproduction of the TRS-80's own
  encoding, just rendered into an 8x8 cell instead of the TRS-80's native
  6x12 (blocks are split roughly 4x4 / 4x4 / 4x2 to fit).
- **U+2500–U+257F range, light single-line subset only** (─│┌┐└┘├┤┬┴┼) —
  VT100/box-drawing characters, also computed: each glyph is just "which of
  up/down/left/right arms touch the center," drawn as 1px (scaled) line
  segments. Only the 11 light single-line glyphs are supported; the rest of
  the Unicode box-drawing block (double lines, dashed, heavy weight, arcs)
  renders blank — a real gap if double-line borders matter, but covers the
  common case.

```csharp
public static class Font8x8
{
    [Flags] enum Arm { Up = 1, Down = 2, Left = 4, Right = 8 }

    // 95 glyphs x 8 rows, ASCII 32..126. Each byte is one row, MSB = leftmost pixel.
    // Data: any public-domain 8x8 ASCII font (e.g. classic IBM VGA/CP437-
    // derived sets are widely available under public domain / CC0 terms).
    static readonly byte[,] AsciiGlyphs = { /* [glyph 0..94, row 0..7] */ };

    static readonly Dictionary<int, Arm> BoxArms = new()
    {
        [0x2500] = Arm.Left | Arm.Right,               // ─
        [0x2502] = Arm.Up | Arm.Down,                  // │
        [0x250C] = Arm.Down | Arm.Right,                // ┌
        [0x2510] = Arm.Down | Arm.Left,                 // ┐
        [0x2514] = Arm.Up | Arm.Right,                  // └
        [0x2518] = Arm.Up | Arm.Left,                   // ┘
        [0x251C] = Arm.Up | Arm.Down | Arm.Right,        // ├
        [0x2524] = Arm.Up | Arm.Down | Arm.Left,         // ┤
        [0x252C] = Arm.Down | Arm.Left | Arm.Right,      // ┬
        [0x2534] = Arm.Up | Arm.Left | Arm.Right,        // ┴
        [0x253C] = Arm.Up | Arm.Down | Arm.Left | Arm.Right, // ┼
    };

    public static void DrawChar(CanvasHandle h, char c, int x, int y, int scale,
                                 byte r, byte g, byte b)
    {
        int code = c;
        if (code >= 32 && code <= 126)
            DrawAsciiGlyph(h, code - 32, x, y, scale, r, g, b);
        else if (code >= 128 && code <= 191)
            DrawSemigraphics(h, code - 128, x, y, scale, r, g, b);
        else if (BoxArms.TryGetValue(code, out var arms))
            DrawBoxChar(h, arms, x, y, scale, r, g, b);
        // else: unsupported codepoint — blank, same as out-of-range ASCII today
    }

    static void DrawAsciiGlyph(CanvasHandle h, int idx, int x, int y, int scale,
                                byte r, byte g, byte b)
    {
        for (int row = 0; row < 8; row++)
        {
            byte bits = AsciiGlyphs[idx, row];
            for (int col = 0; col < 8; col++)
                if ((bits & (0x80 >> col)) != 0)
                    FillCell(h, x, y, col, row, 1, 1, scale, r, g, b);
        }
    }

    static readonly (int x0, int w)[] SemiCols = { (0, 4), (4, 4) };
    static readonly (int y0, int h)[] SemiRows = { (0, 3), (3, 3), (6, 2) };

    static void DrawSemigraphics(CanvasHandle h, int bits, int x, int y, int scale,
                                  byte r, byte g, byte b)
    {
        // bit order matches the TRS-80's own encoding: 0=top-left, 1=top-right,
        // 2=mid-left, 3=mid-right, 4=bottom-left, 5=bottom-right
        for (int cell = 0; cell < 6; cell++)
        {
            if ((bits & (1 << cell)) == 0) continue;
            var (cx, cw) = SemiCols[cell % 2];
            var (cyy, ch) = SemiRows[cell / 2];
            FillCell(h, x, y, cx, cyy, cw, ch, scale, r, g, b);
        }
    }

    static void DrawBoxChar(CanvasHandle h, Arm arms, int x, int y, int scale,
                             byte r, byte g, byte b)
    {
        const int c = 3;   // center row/col of the 8x8 cell
        if ((arms & Arm.Up) != 0)    FillCell(h, x, y, c, 0, 1, c, scale, r, g, b);
        if ((arms & Arm.Down) != 0)  FillCell(h, x, y, c, c, 1, 8 - c, scale, r, g, b);
        if ((arms & Arm.Left) != 0)  FillCell(h, x, y, 0, c, c, 1, scale, r, g, b);
        if ((arms & Arm.Right) != 0) FillCell(h, x, y, c, c, 8 - c, 1, scale, r, g, b);
    }

    // Fills an unscaled col/row rectangle within the 8x8 cell, at (x,y) scaled up.
    static void FillCell(CanvasHandle h, int x, int y, int col, int row, int w, int hgt,
                          int scale, byte r, byte g, byte b)
    {
        for (int py = row; py < row + hgt; py++)
            for (int px = col; px < col + w; px++)
                for (int sy = 0; sy < scale; sy++)
                    for (int sx = 0; sx < scale; sx++)
                        SetPixelClamped(h, x + px * scale + sx, y + py * scale + sy, r, g, b);
    }

    public static void DrawText(CanvasHandle h, string text, int x, int y, int scale,
                                 byte r, byte g, byte b)
    {
        int cx = x;
        foreach (char c in text)
        {
            DrawChar(h, c, cx, y, scale, r, g, b);
            cx += 8 * scale;   // fixed advance — monospace only, no kerning
        }
    }
}
```

`SetPixelClamped` is the same out-of-bounds-silently-clamps logic `CANVASPIXEL`
already needs — factor it out and share it rather than duplicating.

Since this has zero dependencies, `CANVASTEXT` calls `Font8x8.DrawText`
directly from `Engine.cs`. No hook on `CanvasHandle`, no involvement from the
mill's rendering layer at all — it works identically whether a window is open
or not, including under `mill dap`.

### 4. New builtins — add to `Engine.cs` and `Engine.BuiltinWords`

```
CANVASOPEN   ( width height -- canvas )
    If CanvasRegistry.CreateCanvas is non-null, calls it to open a window
    and returns the handle. Otherwise creates a headless CanvasHandle
    with null OnBlit (no-op). Returns Canvas value.

CANVASPIXEL  ( canvas x y r g b -- canvas )
    Writes one RGBA pixel into canvas.Pixels. Pure memory write.
    Clamps out-of-bounds silently. Pushes canvas back.

CANVASFILL   ( canvas r g b -- canvas )
    Fills the entire pixel buffer. Pushes canvas back.

CANVASBLIT   ( canvas -- canvas )
    Calls canvas.OnBlit(pixels, w, h) if non-null; no-op otherwise.
    Pushes canvas back.

CANVASEVENT  ( canvas -- canvas event )
    Non-blocking drain of canvas.Events.
    Pushes a MouseDown / MouseMove / MouseUp / KeyDown / KeyUp / Quit
    Shoddy record (defined in machines/canvas.shoddy), or Nothing if the
    queue is empty. Use whatever Nothing idiom already exists in the
    codebase.

CANVASCLOSE  ( canvas -- )
    Calls canvas.OnClose if non-null. No-op otherwise.

CANVASSIZE   ( canvas -- canvas w h )
    Pushes canvas.Width and canvas.Height. Pure read of fields already
    on the handle — no mill involvement, works headless too. Needed
    because Canvas is an opaque VType (unlike a Shoddy Rec), so Width/
    Height aren't otherwise reachable from Shoddy code.

CANVASGETPIXEL  ( canvas x y -- canvas r g b )
    Reads back one RGBA pixel (alpha dropped) from canvas.Pixels.
    Out-of-bounds reads clamp to 0,0,0. Pure memory read. Required for
    FloodFill and any hit-testing against what's already been drawn —
    without it, drawn pixels are write-only from Shoddy's side.

CANVASTITLE  ( canvas title -- canvas )
    Sets canvas.Title and calls canvas.OnSetTitle(title) if non-null.
    No-op (beyond storing the field) when headless.

CANVASTEXT  ( canvas x y scale r g b text -- canvas )
    Calls Font8x8.DrawText(canvas, text, x, y, scale, r, g, b). Pure memory
    write, same as CANVASPIXEL — no mill involvement, works headless too.
    scale is an integer pixel multiplier (1 = 8x8 px/char, 2 = 16x16, ...),
    not a point size — glyphs are a fixed 8x8 cell, nearest-neighbor scaled,
    not smoothly resized. Supports printable ASCII (32-126, bitmap), TRS-80
    semigraphics (128-191, Chr(128)..Chr(191), computed block mosaic), and
    the 11 light single-line box-drawing characters at U+2500-U+253C
    (Chr(0x2500) etc., computed). Anything else renders blank.
```

---

## Mill's rendering layer (`Shoddy.Mill`)

Add to `Shoddy.Mill.csproj`:
```xml
<PackageReference Include="Silk.NET.Windowing" Version="2.*" />
<PackageReference Include="Silk.NET.Input" Version="2.*" />
<PackageReference Include="Silk.NET.OpenGL" Version="2.*" />
```

`Silk.NET.OpenGL` is used only as a *presentation* mechanism — uploading the
software pixel buffer as a texture and drawing one textured quad. All actual
drawing (`CANVASPIXEL`, `CANVASFILL`, `CANVASTEXT`) writes straight into
`canvas.Pixels` on the CPU, entirely in `Shoddy.Runtime`; nothing is
GPU-rendered, and the mill's rendering layer never touches pixel content —
it only ever reads the finished buffer to blit it.

Add these files to `src/Shoddy.Mill/`:

**`MainThreadDispatcher.cs`** — the one piece of framework glue this design
needs, replacing what `Dispatcher.UIThread` gave for free:
```csharp
public static class MainThreadDispatcher
{
    static readonly ConcurrentQueue<Action> _queue = new();

    public static void Post(Action a) => _queue.Enqueue(a);

    public static void Invoke(Action a)
    {
        using var done = new ManualResetEventSlim(false);
        _queue.Enqueue(() => { a(); done.Set(); });
        done.Wait();
    }

    public static void DrainOnMainThread()
    {
        while (_queue.TryDequeue(out var a)) a();
    }
}
```

**`CanvasWindows.cs`** — tracks every open window so `Main` knows when it's
safe to exit:
```csharp
public static class CanvasWindows
{
    static readonly List<CanvasWindow> _open = new();
    public static bool AnyOpen => _open.Count > 0;

    public static void Register(CanvasWindow w) => _open.Add(w);
    public static void Unregister(CanvasWindow w) => _open.Remove(w);

    public static void PumpAll()
    {
        foreach (var w in _open.ToArray())
            w.Pump();
    }
}
```

**`CanvasWindow.cs`** — one open canvas window:
- Wraps a Silk.NET `IWindow` (`Silk.NET.Windowing.Window.Create(WindowOptions...)`)
  plus an `IInputContext` from `window.CreateInput()`. Construction (window +
  GL context + input context) happens on the main thread — this is the piece
  `MainThreadDispatcher.Invoke` marshals onto in `CanvasRegistry.CreateCanvas`.
- On creation: `window.Initialize()`, get a `GL` via `GL.GetApi(window)`,
  compile the textured-quad shader below, create one texture sized
  `Width x Height`, upload once with empty data.
- `handle.OnBlit` = `Post` a "mark dirty" action. The actual `glTexSubImage2D`
  re-upload + redraw happens in `Pump()` on the main thread — GL calls are
  thread-affine to the context, so this can't run inline on the Shoddy thread.
- `handle.OnClose` = `Post` a request to destroy the GL resources, close the
  `IWindow`, and call `CanvasWindows.Unregister(this)`. Idempotent — safe to
  call twice (once from a user-initiated close, once from `CANVASCLOSE`).
- `handle.OnSetTitle` = `Post(() => window.Title = title)`.
- `Pump()` (called every main-loop tick): `window.DoEvents()`; if dirty,
  re-upload the texture and `window.DoRender()` to draw the quad.
- Mouse: `input.Mice[0].MouseDown` / `MouseMove` / `MouseUp` → push into
  `handle.Events`, mapping Silk.NET's `MouseButton` to 1/2/3.
- Keyboard: `input.Keyboards[0].KeyDown` / `KeyUp` → push into `handle.Events`.
  `KeyChar` is derived directly from the Silk.NET `Key` enum for basic
  printable keys (letters, digits, common punctuation) rather than listening
  for a separate text-input event stream — good enough for hotkey-style
  input; it does not handle IME or shift-modified punctuation correctly.
- `window.Closing` → push a `Quit` event and run the same teardown as
  `OnClose`.

Note `CanvasWindow` has nothing to do with text at all — `CANVASTEXT` writes
into `canvas.Pixels` via `Font8x8` entirely inside `Shoddy.Runtime` (see
above), same as `CANVASPIXEL`/`CANVASFILL`. The mill only ever blits whatever
is already in the buffer.

Blit shader (GLSL, compiled once per window at creation):
```glsl
// vertex
#version 330 core
layout (location = 0) in vec2 aPos;
layout (location = 1) in vec2 aUV;
out vec2 uv;
void main() { uv = aUV; gl_Position = vec4(aPos, 0.0, 1.0); }

// fragment
#version 330 core
in vec2 uv;
out vec4 FragColor;
uniform sampler2D tex;
void main() { FragColor = texture(tex, uv); }
```
Vertex data is one fullscreen quad (two triangles) with UVs running `0..1`.
`Pixels` is row-major top-down — `(x, y)` with `y` increasing downward,
matching `CANVASPIXEL`'s screen-coordinate convention — while GL texture
space is bottom-up by default. Flip `V` in the UV data (or in the shader),
or the image renders upside down.

---

## `machines/canvas.shoddy`

```basic
Include "seq.shoddy"

Rem ---- event types -------------------------------------------------------

Type MouseDown
    X As Number
    Y As Number
    Button As Number    Rem 1=left 2=right 3=middle

Type MouseMove
    X As Number
    Y As Number

Type MouseUp
    X As Number
    Y As Number
    Button As Number

Type KeyDown
    Key As Number       Rem Silk.NET Key enum value, passed through as-is
    KeyChar As String   Rem printable char, or "" for arrows/modifiers/etc.

Type KeyUp
    Key As Number
    KeyChar As String

Type Quit

Type Point
    X As Number
    Y As Number

Rem ---- higher-level drawing (implement in Shoddy using CanvasPixel) ------

Def DrawHLine(cv As Canvas, y As Number, x0 As Number, x1 As Number,
              r As Number, g As Number, b As Number) As Canvas

Def DrawVLine(cv As Canvas, x As Number, y0 As Number, y1 As Number,
              r As Number, g As Number, b As Number) As Canvas

Def DrawLine(cv As Canvas, x0 As Number, y0 As Number, x1 As Number, y1 As Number,
             r As Number, g As Number, b As Number) As Canvas
    Rem general case (Bresenham) — DrawHLine/DrawVLine alone can't do diagonals

Def DrawRect(cv As Canvas, x As Number, y As Number,
             w As Number, h As Number,
             r As Number, g As Number, b As Number) As Canvas
    Rem unfilled rectangle outline

Def FillRect(cv As Canvas, x As Number, y As Number,
             w As Number, h As Number,
             r As Number, g As Number, b As Number) As Canvas

Def DrawCircle(cv As Canvas, cx As Number, cy As Number, radius As Number,
                r As Number, g As Number, b As Number) As Canvas
    Rem midpoint circle algorithm, unfilled

Def FillCircle(cv As Canvas, cx As Number, cy As Number, radius As Number,
                r As Number, g As Number, b As Number) As Canvas

Def DrawPolyline(cv As Canvas, points As List Of Point,
                  r As Number, g As Number, b As Number) As Canvas
    Rem open path: DrawLine between each consecutive pair, no closing edge

Def DrawPolygon(cv As Canvas, points As List Of Point,
                 r As Number, g As Number, b As Number) As Canvas
    Rem DrawPolyline plus one closing edge from last point back to first

Def FloodFill(cv As Canvas, x As Number, y As Number,
               r As Number, g As Number, b As Number) As Canvas
    Rem read the seed pixel with CanvasGetPixel, recursively fill matching
    Rem neighbors — needs no extra state since CanvasGetPixel/CanvasPixel
    Rem read and write the same buffer the recursion is walking
```

Compile with: `mill machine machines/canvas.shoddy`

---

## Example program (`tst/canvas-demo.shoddy`)

```basic
Include "machines/canvas.shoddy"

Def Main()
    Let cv = CanvasOpen(640, 480)
    Let cv = CanvasTitle(cv, "Shoddy Canvas Demo")
    Let cv = CanvasFill(cv, 20, 20, 40)
    Let cv = CanvasText(cv, 10, 10, 2, 255, 255, 255, "Click to paint, Q to quit")
    EventLoop(cv)

Def EventLoop(cv As Canvas)
    Select Case CanvasEvent(cv)
        Case MouseDown(x, y, btn)
            EventLoop(CanvasBlit(FillRect(cv, x - 2, y - 2, 5, 5, 255, 200, 0)))
        Case KeyDown(key, ch)
            If ch = "q" Then
                CanvasClose(cv)
            Else
                EventLoop(CanvasBlit(cv))
        Case Quit
            CanvasClose(cv)
        Case Else
            EventLoop(CanvasBlit(cv))
```

Run with: `mill run tst/canvas-demo.shoddy`

---

## Checklist

- [ ] `VType.Canvas` added to `Model.cs`; `Value` carries `CanvasHandle?`
- [ ] `CanvasHandle`, `CanvasEvent`, `CanvasRegistry` in `Shoddy.Runtime` (no Silk.NET)
- [ ] `Font8x8.cs` added to `Shoddy.Runtime` with the embedded ASCII glyph
      table, computed TRS-80 semigraphics (128-191), computed light
      box-drawing (U+2500-U+253C), `DrawChar`/`DrawText`, and the shared
      out-of-bounds-clamped pixel write
- [ ] 10 builtins added to `Engine.cs` + `Engine.BuiltinWords`: `CANVASOPEN`,
      `CANVASPIXEL`, `CANVASFILL`, `CANVASBLIT`, `CANVASEVENT`, `CANVASCLOSE`,
      `CANVASSIZE`, `CANVASGETPIXEL`, `CANVASTITLE`, `CANVASTEXT`
- [ ] `Shoddy.Mill.csproj` references `Silk.NET.Windowing`, `Silk.NET.Input`,
      `Silk.NET.OpenGL` (no SkiaSharp — text needs nothing from the mill)
- [ ] `Program.cs` runs the Shoddy program on a background thread and drives
      the main-thread loop (`MainThreadDispatcher.DrainOnMainThread` +
      `CanvasWindows.PumpAll`), except for `mill dap` which bypasses it
- [ ] `MainThreadDispatcher.cs` and `CanvasWindows.cs` added; `CanvasRegistry.CreateCanvas`
      is set once at startup and uses `MainThreadDispatcher.Invoke` to create windows
- [ ] `CanvasWindow.cs` renders pixels via the GL blit shader and pushes
      mouse *and* keyboard events; it never touches `CANVASTEXT`
- [ ] Canvas builtins no-op when `CanvasRegistry.CreateCanvas` is null,
      *except* `CANVASTEXT`/`CANVASPIXEL`/`CANVASFILL`/`CANVASGETPIXEL`/
      `CANVASSIZE`, which are pure memory ops and work headless too
- [ ] `mill run`, `mill dap`, `mill weave`, `mill machine` all unaffected for non-canvas programs
- [ ] `machines/canvas.shoddy` compiles cleanly (`DrawLine`, `DrawCircle`,
      `FillCircle`, `DrawPolyline`, `DrawPolygon`, `FloodFill` included)
- [ ] `tst/canvas-demo.shoddy` runs, draws pixels on mouse click, renders
      title text, and quits on `Q`
