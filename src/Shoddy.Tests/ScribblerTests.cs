// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The scribbler, entirely headless: the pure-buffer builtins, Font8x8,
/// the timing builtins, the event decode in machines/scribbler.shoddy,
/// and the INPUT-while-open guard. No window, no GL — a fake
/// CreateScribbler hands back bare handles. Joins the "golden"
/// collection because ScribblerRegistry is process-global static state
/// (OpenCount would trip the INPUT guard under a concurrently-running
/// gradebook test).
/// </summary>
[Collection("golden")]
public class ScribblerTests
{
    static readonly string Root = RepoRoot.Dir;

    // ---- pure-buffer builtins, via woven programs ----------------------

    [Fact]
    public void DrawLineAndFloodFillUseIfteInCallForm()
    {
        // DrawLine (Bresenham) and FloodFill are the only call-form Ifte(...)
        // users in the stdlib; both step through LineStep/AddSeeds. A diagonal
        // lights its endpoints and midpoint but not off-diagonal pixels; a
        // flood from an interior seed recolours the whole black field. These
        // are machine Defs, so the program Includes scribbler.shoddy.
        string ws = MachineWorkspace();
        string program = Path.Combine(ws, "draw.shoddy");
        File.WriteAllText(program, string.Join('\n',
            "Include \"machines/scribbler.shoddy\"",
            "",
            "Def Main()",
            "    Let sc = ScribblerOpen(16, 16)",
            "    Let a = DrawLine(sc, 0, 0, 5, 5, 255, 255, 255)",
            "    Print(ScribblerGetPixel(a, 0, 0))",
            "    Print(ScribblerGetPixel(a, 3, 3))",
            "    Print(ScribblerGetPixel(a, 5, 5))",
            "    Print(ScribblerGetPixel(a, 0, 5))",       // off the diagonal: black
            "    Let b = FloodFill(a, 10, 10, 7, 8, 9)",   // fills the black region
            "    Print(ScribblerGetPixel(b, 15, 15))",
            "    Print(ScribblerGetPixel(b, 3, 3))",       // the white line survives
            ""));
        string output = RunFile(program);
        Assert.Equal(string.Join('\n',
            "Array(255, 255, 255)",
            "Array(255, 255, 255)",
            "Array(255, 255, 255)",
            "Array(0, 0, 0)",
            "Array(7, 8, 9)",
            "Array(255, 255, 255)",
            ""), output);
    }

    [Fact]
    public void FillGetPixelRoundTripAndBounds()
    {
        string output = RunHeadless(string.Join('\n',
            "Def Main()",
            "    Let sc = ScribblerOpen(8, 6)",
            "    Let sc2 = ScribblerFill(sc, 10, 20, 30)",
            "    Print(ScribblerGetPixel(sc2, 0, 0))",
            "    Print(ScribblerGetPixel(sc2, 7, 5))",
            "    Print(ScribblerGetPixel(sc2, 8, 6))",       // OOB read: 0,0,0
            "    Print(ScribblerGetPixel(sc2, -1, 0))",      // OOB read: 0,0,0
            "    Let sc3 = ScribblerPixel(sc2, -5, -5, 1, 2, 3)",   // OOB write: dropped
            "    Let sc4 = ScribblerPixel(sc3, 99, 99, 1, 2, 3)",   // OOB write: dropped
            "    Let sc5 = ScribblerPixel(sc4, 3, 2, 200, 100, 50)",
            "    Print(ScribblerGetPixel(sc5, 3, 2))",
            "    Print(ScribblerWidth(sc5))",
            "    Print(ScribblerHeight(sc5))",
            "    Print(sc5)",                                 // Repr, not blank
            ""));
        Assert.Equal(string.Join('\n',
            "Array(10, 20, 30)",
            "Array(10, 20, 30)",
            "Array(0, 0, 0)",
            "Array(0, 0, 0)",
            "Array(200, 100, 50)",
            "8",
            "6",
            "Scribbler(8, 6)",
            ""), output);
    }

    [Fact]
    public void OpenRaisesWithoutABackend()
    {
        // Woven programs catch ShoddyError, print to stderr, exit 1.
        string err = RunHeadlessError(string.Join('\n',
            "Def Main()",
            "    Let sc = ScribblerOpen(4, 4)",
            "    Print(sc)",
            ""), backend: false);
        Assert.Contains("no window backend", err);
    }

    [Fact]
    public void WaitRaisesHeadless()
    {
        string err = RunHeadlessError(string.Join('\n',
            "Def Main()",
            "    Print(ScribblerWait(ScribblerOpen(4, 4)))",
            ""));
        Assert.Contains("ScribblerWait", err);
    }

    // ---- Font8x8, direct ------------------------------------------------

    static ScribblerHandle NewHandle(int w, int h) =>
        new() { Width = w, Height = h, Pixels = new byte[w * h * 4] };

    static (byte R, byte G, byte B) Px(ScribblerHandle h, int x, int y)
    {
        int i = (y * h.Width + x) * 4;
        return (h.Pixels[i], h.Pixels[i + 1], h.Pixels[i + 2]);
    }

    [Fact]
    public void AsciiGlyphRenders()
    {
        // '!' row 0 is 0x18, LSB-first: pixels at x = 3 and 4.
        ScribblerHandle h = NewHandle(16, 16);
        Font8x8.DrawText(h, "!", 0, 0, 1, 255, 0, 0);
        Assert.Equal(((byte)255, (byte)0, (byte)0), Px(h, 3, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Px(h, 4, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 7, 0));
    }

    [Fact]
    public void SemigraphicGlyphRenders()
    {
        // 129 = 128 + bit 0: the top-left sub-block only — x 0..3, y 0..2.
        ScribblerHandle h = NewHandle(16, 16);
        Font8x8.DrawChar(h, 129, 0, 0, 1, 0, 255, 0);
        Assert.Equal(((byte)0, (byte)255, (byte)0), Px(h, 0, 0));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Px(h, 3, 2));
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 4, 0));    // top-right empty
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 0, 3));    // mid-left empty
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 7, 7));    // bottom-right empty
    }

    [Fact]
    public void BoxDrawingGlyphRenders()
    {
        // U+2500 (─): the centre row (3), full width; nothing vertical.
        ScribblerHandle h = NewHandle(16, 16);
        Font8x8.DrawChar(h, 0x2500, 0, 0, 1, 0, 0, 255);
        Assert.Equal(((byte)0, (byte)0, (byte)255), Px(h, 0, 3));
        Assert.Equal(((byte)0, (byte)0, (byte)255), Px(h, 7, 3));
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 3, 0));
        // U+2502 (│): the centre column, full height.
        Font8x8.DrawChar(h, 0x2502, 8, 8, 1, 0, 0, 255);
        Assert.Equal(((byte)0, (byte)0, (byte)255), Px(h, 11, 8));
        Assert.Equal(((byte)0, (byte)0, (byte)255), Px(h, 11, 15));
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 8, 8));
        // Double-line box drawing renders blank.
        Font8x8.DrawChar(h, 0x2550, 0, 8, 1, 255, 255, 255);
        Assert.Equal(((byte)0, (byte)0, (byte)0), Px(h, 0, 11));
    }

    [Fact]
    public void TextViaBuiltinRendersAtOffsetAndScale()
    {
        string output = RunHeadless(string.Join('\n',
            "Def Main()",
            "    Let sc = ScribblerOpen(64, 32)",
            // '_' (0x5F) is a full bottom row: at scale 2, offset (4, 6),
            // row 7 doubles to y = 6 + 14 and 6 + 15, x = 4..19.
            "    Let sc2 = ScribblerText(sc, 4, 6, 2, 9, 8, 7, \"_\")",
            "    Print(ScribblerGetPixel(sc2, 4, 20))",
            "    Print(ScribblerGetPixel(sc2, 19, 21))",
            "    Print(ScribblerGetPixel(sc2, 4, 19))",       // above the bar: empty
            ""));
        Assert.Equal(string.Join('\n',
            "Array(9, 8, 7)",
            "Array(9, 8, 7)",
            "Array(0, 0, 0)",
            ""), output);
    }

    // ---- timing builtins ------------------------------------------------

    [Fact]
    public void TicksSleepClock()
    {
        string output = RunHeadless(string.Join('\n',
            "Def Main()",
            "    Let t0 = Ticks()",
            "    Sleep(60)",
            "    Let t1 = Ticks()",
            "    Print(t1 >= t0)",
            "    Print(t1 - t0 >= 50)",
            "    Let c = Clock()",
            "    Print(Length(c))",
            "    Print(Nth(c, 1) >= 2026)",
            "    Print(Nth(c, 2) >= 1 And Nth(c, 2) <= 12)",
            "    Print(Nth(c, 3) >= 1 And Nth(c, 3) <= 31)",
            "    Print(Nth(c, 4) >= 0 And Nth(c, 4) <= 23)",
            "    Print(Nth(c, 5) >= 0 And Nth(c, 5) <= 59)",
            "    Print(Nth(c, 6) >= 0 And Nth(c, 6) <= 60)",
            "    Print(Nth(c, 7) >= 0 And Nth(c, 7) <= 999)",
            ""), backend: false);
        Assert.Equal(string.Join('\n',
            "True", "True", "7", "True", "True", "True", "True", "True", "True", "True",
            ""), output);
    }

    // ---- event decode via machines/scribbler.shoddy ---------------------

    [Fact]
    public void MachineDecodesEventsIntoConstructors()
    {
        string ws = MachineWorkspace();
        string program = Path.Combine(ws, "decode.shoddy");
        File.WriteAllText(program, string.Join('\n',
            "Include \"machines/scribbler.shoddy\"",
            "",
            "Def Show(e As ScribblerEvent)",
            "    Select Case e",
            "        Case ScribblerMouseDown(x, y, btn, mods, at)",
            "            Print(\"down \" & Str(x) & \" \" & Str(y) & \" \" & Str(btn) & \" \" & Str(mods) & \" \" & Str(at))",
            "        Case ScribblerMouseMove(x, y, mods, at)",
            "            Print(\"move \" & Str(x) & \" \" & Str(y) & \" \" & Str(mods) & \" \" & Str(at))",
            "        Case ScribblerKeyDown(key, mods, at)",
            "            Print(\"key \" & Str(key) & \" \" & Str(mods) & \" \" & Str(at))",
            "        Case ScribblerTyped(ch, mods, at)",
            "            Print(\"typed \" & ch & \" \" & Str(mods) & \" \" & Str(at))",
            "        Case ScribblerTick(at)",
            "            Print(\"tick \" & Str(at))",
            "        Case ScribblerQuit",
            "            Print(\"quit\")",
            "        Case ScribblerNone",
            "            Print(\"none\")",
            "        Case Else",
            "            Print(\"other\")",
            "",
            "Def Main()",
            "    Show(DecodeEvent(ToArray({ 1, 10, 20, 1, 0, \"\", 3, 125.5 })))",
            "    Show(DecodeEvent(ToArray({ 2, 5, 6, 0, 0, \"\", 0, 7 })))",
            "    Show(DecodeEvent(ToArray({ 4, 0, 0, 0, 262, \"\", 1, 8 })))",
            "    Show(DecodeEvent(ToArray({ 6, 0, 0, 0, 0, \"q\", 8, 2 })))",
            "    Show(DecodeEvent(ToArray({ 7, 0, 0, 0, 0, \"\", 0, 99 })))",
            "    Show(DecodeEvent(ToArray({ 8, 0, 0, 0, 0, \"\", 0, 0 })))",
            "    Show(DecodeEvent(ToArray({ 0, 0, 0, 0, 0, \"\", 0, 0 })))",
            ""));
        string output = RunFile(program);
        Assert.Equal(string.Join('\n',
            "down 10 20 1 3 125.5",
            "move 5 6 0 7",
            "key 262 1 8",
            "typed q 8 2",
            "tick 99",
            "quit",
            "none",
            ""), output);
    }

    [Fact]
    public void PeekEventDrainsAQueueHeadless()
    {
        string ws = MachineWorkspace();
        string program = Path.Combine(ws, "peek.shoddy");
        File.WriteAllText(program, string.Join('\n',
            "Include \"machines/scribbler.shoddy\"",
            "",
            "Def Main()",
            "    Let sc = ScribblerOpen(4, 4)",
            "    Select Case PeekEvent(sc)",
            "        Case ScribblerMouseDown(x, y, btn, mods, at)",
            "            Print(\"down \" & Str(x) & \",\" & Str(y))",
            "        Case Else",
            "            Print(\"wrong\")",
            "    Select Case PeekEvent(sc)",
            "        Case ScribblerNone",
            "            Print(\"empty\")",
            "        Case Else",
            "            Print(\"wrong\")",
            ""));
        string output = RunFile(program, preloaded: h => h.Events.Enqueue(new ScribblerEvent
        {
            Type = ScribblerEvent.Kind.MouseDown, X = 3, Y = 1, Button = 1, At = 5,
        }));
        Assert.Equal("down 3,1\nempty\n", output);
    }

    // ---- input ownership ------------------------------------------------

    [Fact]
    public void InputRefusedWhileAScribblerIsOpen()
    {
        string err = RunHeadlessError(string.Join('\n',
            "Def Main()",
            "    Let sc = ScribblerOpen(4, 4)",
            "    Print(Input(\"? \"))",
            ""));
        Assert.Contains("ScribblerWait or ScribblerPoll", err);

        // Closed again (or never opened): Input works as before.
        string output = RunHeadless(string.Join('\n',
            "Def Main()",
            "    Let sc = ScribblerClose(ScribblerOpen(4, 4))",
            "    Print(Input(\"? \"))",
            ""), input: new StringReader("hello\n"));
        Assert.Equal("? hello\n", output);
    }

    [Fact]
    public void CloseIsIdempotent()
    {
        string output = RunHeadless(string.Join('\n',
            "Def Main()",
            "    Let sc = ScribblerOpen(4, 4)",
            "    Let a = ScribblerClose(sc)",
            "    Let b = ScribblerClose(a)",       // second close: no-op
            "    Print(Input(\"\"))",              // OpenCount back to 0 exactly
            ""), input: new StringReader("ok\n"));
        Assert.Equal("ok\n", output);
        Assert.Equal(0, ScribblerRegistry.OpenCount);
    }

    [Fact]
    public void WaitAllClosedBlocksUntilTheLastClose()
    {
        // The perch holds the terminated event on this: it must block
        // while any scribbler is open and wake on the last NoteClosed,
        // shrugging off stale ticks left by earlier closes.
        ScribblerRegistry.OpenCount = 2;
        var released = new ManualResetEventSlim(false);
        var waiter = new Thread(() => { ScribblerRegistry.WaitAllClosed(); released.Set(); })
        { IsBackground = true };
        waiter.Start();
        Assert.False(released.Wait(50));
        ScribblerRegistry.NoteClosed();
        Assert.False(released.Wait(50));
        ScribblerRegistry.NoteClosed();
        Assert.True(released.Wait(5000));
        Assert.Equal(0, ScribblerRegistry.OpenCount);

        // And with nothing open it returns immediately.
        ScribblerRegistry.WaitAllClosed();
    }

    // ---- the machines compile ------------------------------------------

    [Fact]
    public void ClockAndScribblerMachinesCompile()
    {
        // scribbler first: with no machine DLLs in the workspace yet, its
        // includes (seq, clock) splice as source, so nothing is assembly-
        // loaded — loading Shoddy.Machines.Seq here would collide with the
        // copy MachineTests loads from its own workspace (one name, one
        // default load context per process).
        string ws = MachineWorkspace();
        foreach (string name in new[] { "scribbler", "clock" })
        {
            string sb = Path.Combine(ws, "machines", name + ".shoddy");
            var (prog, machines) = ParseWithMachines(sb);
            Weaver.WeaveMachine(prog, sb, machines.Machines);
        }
        Assert.True(File.Exists(
            MachineSet.DllPathFor(Path.Combine(ws, "machines", "scribbler.shoddy"))));
        Assert.True(File.Exists(
            MachineSet.DllPathFor(Path.Combine(ws, "machines", "clock.shoddy"))));
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>A workspace with a copy of machines/*.shoddy, so Include
    /// resolution works without touching the repo tree.</summary>
    static string MachineWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-scribbler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        return ws;
    }

    /// <summary>Weave and run source with a fake window backend: bare
    /// handles, no window, no GL — exactly what SCRIBBLEROPEN needs to
    /// exercise the pure-buffer path. Registry state is restored so the
    /// serial golden tests never see a stale OpenCount.</summary>
    static string RunHeadless(string src, bool backend = true, TextReader? input = null)
    {
        var (exit, output, err) = Execute(WriteProgram(src), backend, input, null);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {err}");
        return output;
    }

    /// <summary>Run a program expected to die with a ShoddyError: woven
    /// programs catch it themselves, print to stderr and return 1.</summary>
    static string RunHeadlessError(string src, bool backend = true)
    {
        var (exit, _, err) = Execute(WriteProgram(src), backend, null, null);
        Assert.Equal(1, exit);
        return err;
    }

    static string RunFile(string path, Action<ScribblerHandle>? preloaded = null)
    {
        var (exit, output, err) = Execute(path, backend: true, null, preloaded);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {err}");
        return output;
    }

    static string WriteProgram(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-scribbler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sb = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(sb, src);
        return sb;
    }

    static (int Exit, string Out, string Err) Execute(string path, bool backend,
                                                      TextReader? input,
                                                      Action<ScribblerHandle>? preloaded)
    {
        ScribblerRegistry.CreateScribbler = backend
            ? (w, h) =>
              {
                  var handle = new ScribblerHandle();
                  preloaded?.Invoke(handle);
                  return handle;
              }
            : null;
        ScribblerRegistry.OpenCount = 0;
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var machines = new MachineSet();
            var lines = Lexer.ReadProgram(path, machines.TryResolve);
            var prog = new ShoddyProgram();
            machines.SeedInto(prog);
            Parser.Parse(lines, prog);
            var output = new StringWriter();
            int exit = Weaver.Execute(prog, machines.Machines, output,
                                      input ?? TextReader.Null, Array.Empty<string>());
            return (exit, output.ToString(), err.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
            ScribblerRegistry.CreateScribbler = null;
            ScribblerRegistry.OpenCount = 0;
        }
    }

    static (ShoddyProgram, MachineSet) ParseWithMachines(string file)
    {
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(file, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        return (Parser.Parse(lines, prog), machines);
    }
}
