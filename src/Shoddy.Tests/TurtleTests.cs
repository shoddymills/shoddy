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
/// machines/turtle.shoddy, headless: a fake CreateScribbler hands back
/// bare buffers, so the turtle's drawing (DrawLine into the shared buffer)
/// and pose arithmetic are fully exercised with no window and no GL. Joins
/// the "golden" collection because ScribblerRegistry is process-global.
/// </summary>
[Collection("golden")]
public class TurtleTests
{
    static readonly string Root = RepoRoot.Dir;

    [Fact]
    public void StartPoseAndForwardDrawsUp()
    {
        // Centre of a 20x20 surface is (10,10), heading 0 (up). Forward 5
        // draws a vertical segment up to (10,5); x is unchanged.
        string output = RunTurtle(
            "    Let t = NewTurtle(20, 20)",
            "    Print(Str(Round(X(t))) & \",\" & Str(Round(Y(t))) & \" h\" & Str(Round(Heading(t))))",
            "    Let t2 = Forward(t, 5)",
            "    Print(Str(Round(X(t2))) & \",\" & Str(Round(Y(t2))))",
            "    Print(ScribblerGetPixel(Sc(t2), 10, 10))",   // start, drawn
            "    Print(ScribblerGetPixel(Sc(t2), 10, 7))",    // on the path
            "    Print(ScribblerGetPixel(Sc(t2), 10, 5))",    // endpoint
            "    Print(ScribblerGetPixel(Sc(t2), 12, 10))");  // off the path: black
        Assert.Equal(string.Join('\n',
            "10,10 h0",
            "10,5",
            "Array(255, 255, 255)",
            "Array(255, 255, 255)",
            "Array(255, 255, 255)",
            "Array(0, 0, 0)",
            ""), output);
    }

    [Fact]
    public void RightTurnGoesEastAndPenUpDoesNotDraw()
    {
        string output = RunTurtle(
            "    Let t = TurnRight(NewTurtle(20, 20), 90)",       // face east
            "    Print(Round(Heading(t)))",
            "    Let t2 = Forward(t, 5)",                          // -> (15,10)
            "    Print(Str(Round(X(t2))) & \",\" & Str(Round(Y(t2))))",
            "    Print(ScribblerGetPixel(Sc(t2), 13, 10))",       // drawn
            "    Let t3 = Forward(PenUp(t2), 3)",                  // -> (18,10), no ink
            "    Print(Round(X(t3)))",
            "    Print(ScribblerGetPixel(Sc(t3), 17, 10))");      // black: pen was up
        Assert.Equal(string.Join('\n',
            "90",
            "15,10",
            "Array(255, 255, 255)",
            "18",
            "Array(0, 0, 0)",
            ""), output);
    }

    [Fact]
    public void SquareReturnsToStartAndWrapsHeading()
    {
        // Four Forward+TurnRight(90) — LOGO's REPEAT 4 — closes the figure
        // and the heading comes back to 0 (360 wrapped). Fractional pose is
        // exact, so start and end pixels coincide.
        string output = RunTurtle(
            "    Let t = NewTurtle(30, 30)",
            "    Let sq = Fold(Range(1, 4), t, Fn(t, i) => TurnRight(Forward(t, 6), 90))",
            "    Print(Str(Round(X(sq))) & \",\" & Str(Round(Y(sq))) & \" h\" & Str(Round(Heading(sq))))");
        Assert.Equal("15,15 h0\n", output);
    }

    [Fact]
    public void HeadingWrapsBothWays()
    {
        string output = RunTurtle(
            "    Print(Round(Heading(TurnLeft(NewTurtle(10, 10), 90))))",                  // -90 -> 270
            "    Print(Round(Heading(TurnRight(SetHeading(NewTurtle(10, 10), 350), 20))))", // 370 -> 10
            "    Print(Round(Heading(SetHeading(NewTurtle(10, 10), -45))))");                // -45 -> 315
        Assert.Equal("270\n10\n315\n", output);
    }

    [Fact]
    public void SetTowardsFacesTargetAndDistance()
    {
        // From centre (10,10): up -> 0, east -> 90, down -> 180; and a
        // 3-4-5 triangle gives distance 5.
        string output = RunTurtle(
            "    Let t = NewTurtle(20, 20)",
            "    Print(Round(Heading(SetTowards(t, 10, 0))))",    // straight up
            "    Print(Round(Heading(SetTowards(t, 20, 10))))",   // east
            "    Print(Round(Heading(SetTowards(t, 10, 20))))",   // south
            "    Print(Round(Heading(SetTowards(t, 0, 10))))",    // west
            "    Print(Round(DistanceTo(t, 13, 14)))");           // 3-4-5
        Assert.Equal("0\n90\n180\n270\n5\n", output);
    }

    [Fact]
    public void TurtleMachineCompilesToDll()
    {
        // Weave turtle with its includes (scribbler->seq,clock; math)
        // spliced as SOURCE — no dependency DLLs are pre-built, so no
        // machine assembly is loaded. Building and loading Shoddy.Machines.Seq
        // here would collide with the copy MachineTests loads (one name, one
        // default load context per process) — the same reason ScribblerTests
        // compiles scribbler this way.
        string ws = MachineWorkspace();
        string sb = Path.Combine(ws, "machines", "turtle.shoddy");
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sb, machines.Machines);
        Assert.True(File.Exists(Path.Combine(ws, "machines", "Shoddy.Machines.Turtle.dll")));
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>Weave and run a Main body that Includes turtle.shoddy,
    /// against a fake window backend (bare buffers, no GL). Registry state
    /// is restored so the serial golden tests never see a stale count.</summary>
    static string RunTurtle(params string[] body)
    {
        string ws = MachineWorkspace();
        string sb = Path.Combine(ws, "prog.shoddy");
        File.WriteAllText(sb, "Include \"machines/turtle.shoddy\"\n\nDef Main()\n"
                              + string.Join('\n', body) + "\n");

        ScribblerRegistry.CreateScribbler = (w, h) => new ScribblerHandle { Width = w, Height = h, Pixels = new byte[w * h * 4] };
        ScribblerRegistry.OpenCount = 0;
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var machines = new MachineSet();
            var lines = Lexer.ReadProgram(sb, machines.TryResolve);
            var prog = new ShoddyProgram();
            machines.SeedInto(prog);
            Parser.Parse(lines, prog);
            var output = new StringWriter();
            int exit = Weaver.Execute(prog, machines.Machines, output, TextReader.Null, Array.Empty<string>());
            Assert.True(exit == 0, $"expected exit 0, got {exit}: {err}");
            return output.ToString();
        }
        finally
        {
            Console.SetError(oldErr);
            ScribblerRegistry.CreateScribbler = null;
            ScribblerRegistry.OpenCount = 0;
        }
    }

    static string MachineWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-turtle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        return ws;
    }
}
