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

There is no separate Studio project. The mill gains Avalonia and a canvas window
that opens automatically when a Shoddy program calls `CanvasOpen`. One binary,
one tool.

### Design principles

- **One mill** — `mill run FILE.shoddy` works as today for console programs.
  Canvas programs get a window automatically; non-canvas programs are unaffected.
- **Console I/O unchanged** — `Print`, `Input`, `vt100.shoddy` continue to use
  the console. Pass `Console.Out` and `Console.In` to the Engine as always.
  `OutputType` stays `Exe` so the console window is always present.
- **Debugging unchanged** — `mill dap` is untouched. Canvas builtins must no-op
  gracefully when called via `mill dap` (no Avalonia event loop running).
- **Do not change** `Shoddy.Runtime`, `Dap.cs`, or any existing source files
  beyond `Engine.cs`, `Model.cs`, and `Engine.BuiltinWords` in `Shoddy.Runtime`,
  plus `Program.cs` and `Shoddy.Mill.csproj` in `Shoddy.Mill`.
- **Do not add Avalonia** as a dependency to `Shoddy.Runtime` — keep it pure .NET.

---

## Threading model

Avalonia's event loop must own the main thread. The Shoddy program runs on a
background thread.

`Program.cs` `Main` becomes:

```csharp
// Called by the OS — must be [STAThread] on Windows for Avalonia
[STAThread]
static int Main(string[] args)
{
    // mill dap: no Avalonia, just run the DAP server and exit
    if (args.Length == 1 && args[0] == "dap")
        return DapServer.Serve();

    // All other commands: boot Avalonia (even for non-canvas programs —
    // it will just exit cleanly when the program thread finishes and no
    // canvas window was opened).
    return AppBuilder.Configure<MillApp>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args);
}
```

`MillApp` is a minimal Avalonia `Application` subclass. Its `OnFrameworkInitializationCompleted`
sets `CanvasRegistry.CreateCanvas` (see below) and then starts the Shoddy program
on `Task.Run`. When the program thread finishes and no canvas window is open,
the app shuts down.

---

## New runtime additions (in `Shoddy.Runtime`)

### 1. `VType.Canvas` — add to `Model.cs`

Add `Canvas` to the `VType` enum. Add a `CanvasHandle? Canvas` field to the
`Value` struct.

`CanvasHandle` — pure .NET, no Avalonia reference:

```csharp
public sealed class CanvasHandle
{
    public byte[] Pixels;          // RGBA, row-major
    public int Width, Height;
    public string Title = "";
    public readonly ConcurrentQueue<CanvasEvent> Events = new();

    // Set by the mill's Avalonia layer before Execute; null = no-op
    public Action<byte[], int, int>? OnBlit;   // (pixels, w, h)
    public Action? OnClose;
    public Action<string>? OnSetTitle;         // (title)

    // Rasterizes text into Pixels and returns once done — unlike OnBlit,
    // callers (CANVASTEXT) need the written pixels before they return, so
    // the mill layer must marshal onto the UI thread with Invoke, not Post.
    public Action<string, int, int, int, byte, byte, byte>? OnDrawText;   // (text, x, y, size, r, g, b)
}

public sealed class CanvasEvent
{
    public enum Kind { MouseDown, MouseMove, MouseUp, KeyDown, KeyUp, Quit }
    public Kind Type;
    public int X, Y, Button;
    public int Key;            // KeyDown/KeyUp only: Avalonia Key cast to int
    public string KeyChar = "";  // KeyDown/KeyUp only: printable char if any, else ""
}
```

### 2. `CanvasRegistry` — add to `Shoddy.Runtime`

```csharp
public static class CanvasRegistry
{
    // Mill's Avalonia layer sets this before calling Execute.
    // Null when running headless (mill dap, mill weave, etc.)
    public static Func<int, int, CanvasHandle>? CreateCanvas;
}
```

### 3. New builtins — add to `Engine.cs` and `Engine.BuiltinWords`

```
CANVASOPEN   ( width height -- canvas )
    If CanvasRegistry.CreateCanvas is non-null, calls it to open an Avalonia
    window and returns the handle. Otherwise creates a headless CanvasHandle
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

CANVASTEXT  ( canvas x y size r g b text -- canvas )
    Calls canvas.OnDrawText(text, x, y, size, r, g, b) if non-null,
    which rasterizes the string using the mill's font-rendering layer
    and writes the resulting pixels into canvas.Pixels tinted by r,g,b.
    No-op when headless (font rasterization needs Avalonia; there is no
    pure-.NET text rasterizer in Shoddy.Runtime, so unlike the other
    CANVAS* words this one does nothing at all without a live window).
```

---

## Mill's Avalonia layer (`Shoddy.Mill`)

Add to `Shoddy.Mill.csproj`:
```xml
<PackageReference Include="Avalonia" Version="11.*" />
<PackageReference Include="Avalonia.Desktop" Version="11.*" />
<PackageReference Include="Avalonia.Themes.Fluent" Version="11.*" />
```

Add these files to `src/Shoddy.Mill/`:

**`MillApp.cs`** — minimal Avalonia Application:
```csharp
public class MillApp : Application
{
    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);   // or apply Fluent theme in code

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Register the canvas factory — opens a CanvasWindow on demand.
            CanvasRegistry.CreateCanvas = (w, h) =>
            {
                var handle = new CanvasHandle { ... };
                Dispatcher.UIThread.Post(() => {
                    var win = new CanvasWindow(handle);
                    win.Show();
                });
                return handle;
            };

            // Run the Shoddy program on a background thread.
            // Parse args from desktop.Args (skip "run" / file boilerplate).
            Task.Run(() => {
                int rc = RunShoddy(desktop.Args);
                // If no canvas window is open, shut the app down.
                if (noOpenWindows)
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(rc));
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

**`CanvasWindow.cs`** — minimal Avalonia Window:
- Contains a single `Image` control bound to a `WriteableBitmap`.
- `handle.OnBlit` copies pixel bytes into the bitmap and calls `InvalidateVisual()`.
- `handle.OnClose` calls `Close()`.
- `handle.OnSetTitle` sets `Title` (marshal via `Dispatcher.UIThread.Post`).
- `handle.OnDrawText` renders the string with Avalonia's `FormattedText`
  (or a `RenderTargetBitmap` sized to the glyph run), reads the rendered
  pixels back, alpha-composites them into `handle.Pixels` at `(x, y)`
  tinted by `(r, g, b)`. Must run via `Dispatcher.UIThread.Invoke`
  (blocking), not `Post` — `CANVASTEXT` needs the pixels written before
  it returns, unlike `OnBlit` which is fire-and-forget.
- Mouse events (`PointerPressed`, `PointerReleased`, `PointerMoved`) push into
  `handle.Events`.
- Keyboard events (`KeyDown`, `KeyUp`) push into `handle.Events` with
  `Key` set from the Avalonia `KeyEventArgs.Key` and `KeyChar` set from
  `KeyEventArgs.KeySymbol` when it's a single printable character (empty
  otherwise, e.g. for arrow keys / modifiers). Needs `Focusable="True"`
  on the window or the `Image` control, or key events never arrive.
- On window close (user clicks X), push a `Quit` event and call `handle.OnClose`.

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
    Key As Number       Rem Avalonia Key enum value, passed through as-is
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
    Let cv = CanvasText(cv, 10, 10, 16, 255, 255, 255, "Click to paint, Q to quit")
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
- [ ] `CanvasHandle`, `CanvasEvent`, `CanvasRegistry` in `Shoddy.Runtime` (no Avalonia)
- [ ] 10 builtins added to `Engine.cs` + `Engine.BuiltinWords`: `CANVASOPEN`,
      `CANVASPIXEL`, `CANVASFILL`, `CANVASBLIT`, `CANVASEVENT`, `CANVASCLOSE`,
      `CANVASSIZE`, `CANVASGETPIXEL`, `CANVASTITLE`, `CANVASTEXT`
- [ ] `Shoddy.Mill.csproj` references Avalonia
- [ ] `Program.cs` boots Avalonia (except for `mill dap` which bypasses it)
- [ ] `MillApp.cs` sets `CanvasRegistry.CreateCanvas` and runs Shoddy on background thread
- [ ] `CanvasWindow.cs` renders pixels, pushes mouse *and* keyboard events,
      sets window title, and rasterizes `OnDrawText` synchronously via
      `Dispatcher.UIThread.Invoke`
- [ ] Canvas builtins no-op when `CanvasRegistry.CreateCanvas` is null
      (`CANVASTEXT` no-ops even when a window exists, if `OnDrawText` isn't wired)
- [ ] `mill run`, `mill dap`, `mill weave`, `mill machine` all unaffected for non-canvas programs
- [ ] `machines/canvas.shoddy` compiles cleanly (`DrawLine`, `DrawCircle`,
      `FillCircle`, `DrawPolyline`, `DrawPolygon`, `FloodFill` included)
- [ ] `tst/canvas-demo.shoddy` runs, draws pixels on mouse click, renders
      title text, and quits on `Q`
