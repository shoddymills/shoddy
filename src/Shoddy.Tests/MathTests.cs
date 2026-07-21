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
/// The math primitives added to the runtime (ATN2, ASIN, ACOS, SGN, FIX,
/// LOG10, WRAP, SEED) and the derived machines/math.shoddy layer built on
/// them. Everything here is pure computation — no window, no I/O — so it
/// weaves and runs in-process like any `mill run` program.
/// </summary>
public class MathTests
{
    static readonly string Root = RepoRoot.Dir;

    // ---- new runtime builtins ------------------------------------------

    [Fact]
    public void TrigCompletions()
    {
        // ATN2 recovers a full-circle angle one-arg ATN cannot; ASIN/ACOS
        // complete the inverse trig set. Values scaled to integers.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Round(Atn2(1, 1) * 1000))",     // pi/4  = 785
            "    Print(Round(Atn2(1, -1) * 1000))",    // 3pi/4 = 2356
            "    Print(Round(Atn2(-1, -1) * 1000))",   // -3pi/4 = -2356
            "    Print(Round(Asin(1) * 1000))",        // pi/2  = 1571
            "    Print(Round(Acos(0) * 1000))",        // pi/2  = 1571
            "    Print(Round(Acos(-1) * 1000))",       // pi    = 3142
            ""));
        Assert.Equal("785\n2356\n-2356\n1571\n1571\n3142\n", output);
    }

    [Fact]
    public void SignTruncateAndFlooredMod()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Sgn(-4))",           // -1
            "    Print(Sgn(0))",            // 0
            "    Print(Sgn(9))",            // 1
            "    Print(Fix(-3.9))",         // -3 (toward zero)
            "    Print(Floor(-3.9))",       // -4 (toward -inf) — the contrast
            "    Print(Fix(3.9))",          // 3
            "    Print(Wrap(-10, 360))",    // 350 — carries divisor's sign
            "    Print(Wrap(370, 360))",    // 10
            "    Print(Wrap(-1, 5))",       // 4
            "    Print(-10 Mod 360)",       // -10 — plain MOD stays fmod
            ""));
        Assert.Equal("-1\n0\n1\n-3\n-4\n3\n350\n10\n4\n-10\n", output);
    }

    [Fact]
    public void Log10Builtin()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Round(Log10(1000)))",   // 3
            "    Print(Round(Log10(1)))",      // 0
            ""));
        Assert.Equal("3\n0\n", output);
    }

    [Fact]
    public void SeedMakesRndReproducible()
    {
        // Two runs from the same seed draw the same sequence; a different
        // seed (almost surely) does not. No exact value is asserted — only
        // the reproducibility contract SEED exists to provide.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Seed(42)",
            "    Let a1 = Rnd()",
            "    Let a2 = Rnd()",
            "    Seed(42)",
            "    Let b1 = Rnd()",
            "    Let b2 = Rnd()",
            "    Print(a1 = b1 And a2 = b2)",   // True
            "    Print(a1 <> a2)",              // True — sequence advances
            ""));
        Assert.Equal("True\nTrue\n", output);
    }

    [Fact]
    public void DomainErrorsRaiseRatherThanNaN()
    {
        Assert.Contains("ASIN outside [-1, 1]", RunSrcError("Def Main()\n    Print(Asin(2))\n"));
        Assert.Contains("ACOS outside [-1, 1]", RunSrcError("Def Main()\n    Print(Acos(-2))\n"));
        Assert.Contains("LOG10 of non-positive", RunSrcError("Def Main()\n    Print(Log10(0))\n"));
        Assert.Contains("WRAP by zero", RunSrcError("Def Main()\n    Print(Wrap(5, 0))\n"));
    }

    // ---- the derived machine -------------------------------------------

    [Fact]
    public void MachineLayerComputes()
    {
        string output = RunWithMath(string.Join('\n',
            "Include \"machines/math.shoddy\"",
            "",
            "Def Main()",
            "    Print(Round(Rad(180) * 1000))",             // 3142
            "    Print(Round(Deg(Pi())))",                   // 180
            "    Print(Round(Angle(1, 1) * 1000))",          // atan2(1,1) = 785
            "    Print(Clamp(15, 0, 10))",                   // 10
            "    Print(Clamp(-3, 0, 10))",                   // 0
            "    Print(Lerp(0, 100, 0.25))",                 // 25
            "    Print(InvLerp(0, 100, 25))",                // 0.25
            "    Print(Remap(5, 0, 10, 0, 100))",            // 50
            "    Print(Smoothstep(0, 10, 5))",               // 0.5
            "    Print(RoundTo(3.14159, 2))",                // 3.14
            "    Print(Frac(-3.25))",                        // -0.25
            "    Print(Round(Hypot(3, 4)))",                 // 5
            "    Print(Round(Dist(0, 0, 3, 4)))",            // 5
            "    Print(Round(E() * 1000))",                  // 2718
            "    Print(Round(Tau() * 1000))",                // 6283
            "    Print(Round(Log2(8)))",                     // 3
            "    Print(Round(LogBase(3, 81)))",              // 4
            ""));
        Assert.Equal(string.Join('\n',
            "3142", "180", "785", "10", "0", "25", "0.25", "50", "0.5",
            "3.14", "-0.25", "5", "5", "2718", "6283", "3", "4",
            ""), output);
    }

    [Fact]
    public void MachineRandomStaysInRange()
    {
        // RandInt(lo, hi) is inclusive on both ends; Pick returns a member;
        // Chance is a Boolean. Seeded so the run is deterministic, but only
        // structural facts (range, membership, type) are asserted.
        string output = RunWithMath(string.Join('\n',
            "Include \"machines/seq.shoddy\"",       // All lives here
            "Include \"machines/math.shoddy\"",
            "",
            "Def InRange(x As Number, lo As Number, hi As Number) As Boolean",
            "    x >= lo And x <= hi",
            "",
            "Def Main()",
            "    Seed(7)",
            "    Print(All(Map(Range(1, 200), Fn(i) => RandInt(3, 8)), Fn(x) => InRange(x, 3, 8)))",
            "    Print(RandRange(0, 1) >= 0)",
            "    Print(Pick({ 5, 5, 5 }))",
            "    Print(Chance(1))",     // p >= 1 always True
            "    Print(Chance(0))",     // p <= 0 always False
            ""));
        Assert.Equal("True\nTrue\n5\nTrue\nFalse\n", output);
    }

    [Fact]
    public void MathMachineCompilesToDll()
    {
        string ws = MachineWorkspace();
        string sb = Path.Combine(ws, "machines", "math.shoddy");
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sb, machines.Machines);
        Assert.True(File.Exists(Path.Combine(ws, "machines", "Shoddy.Machines.Math.dll")));
    }

    // ---- helpers --------------------------------------------------------

    static string RunSrc(string src)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string RunSrcError(string src)
    {
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
            int exit = Weaver.Execute(prog, null, new StringWriter(), TextReader.Null, Array.Empty<string>());
            Assert.Equal(1, exit);
            return err.ToString();
        }
        finally { Console.SetError(oldErr); }
    }

    /// <summary>Run source that Includes machines/math.shoddy, resolved
    /// from a workspace copy of the machines tree (spliced as source — the
    /// machine has no includes and needs no prebuilt DLL).</summary>
    static string RunWithMath(string src)
    {
        string ws = MachineWorkspace();
        string sb = Path.Combine(ws, "prog.shoddy");
        File.WriteAllText(sb, src);
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Parser.Parse(lines, prog);
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, machines.Machines, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string MachineWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-math", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        return ws;
    }

    static string WriteTemp(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-math", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sb = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(sb, src);
        return sb;
    }
}
