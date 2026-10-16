// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Hosting;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The perch's machinery, tested at the sink level (below the DAP
/// protocol): line hooks fire with correct file/line/frame data, locals
/// and globals are visible with Shoddy values, and — the constitution
/// again — a debug-instrumented weave produces byte-identical output.
/// </summary>
[Collection("golden")]
public class DebugTests
{
    static readonly string Root = RepoRoot.Dir;

    sealed class Recorder : IDebugSink
    {
        public readonly List<(string File, int Line, int Depth, string Frame)> Hits = new();
        public Action<Engine>? Probe;

        public void OnLine(Engine rt)
        {
            DebugFrame f = rt.Frames[^1];
            Hits.Add((Path.GetFileName(rt.DebugFiles[f.FileId]), f.Line, rt.Frames.Count, f.Name));
            Probe?.Invoke(rt);
        }
    }

    [Fact]
    public void FramesLocalsAndGlobalsAreVisible()
    {
        string src = string.Join('\n',
            "Let base = 40",                    // line 1
            "",
            "Def Square(n As Number) As Number",// line 3
            "    n * n",                        // line 4
            "",
            "Def Main()",                       // line 6
            "    Let x = Square(7)",            // line 7
            "    Print(x + base)",              // line 8
            "");
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-perch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sbPath = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(sbPath, src);

        var rec = new Recorder();
        double? nSeen = null, xSeen = null, baseGlobal = null;
        var frameNames = new List<string>();
        rec.Probe = rt =>
        {
            DebugFrame top = rt.Frames[^1];
            if (top.Name == "SQUARE" && top.Line == 4 && nSeen == null)
            {
                frameNames.AddRange(rt.Frames.Select(f => f.Name));
                nSeen = top.Binds.Last(b => b.Name == "N").V.Num;
            }
            if (top.Name == "MAIN" && top.Line == 8)
            {
                xSeen = top.Binds.Last(b => b.Name == "X").V.Num;
                baseGlobal = rt.Globals.Last(g => g.Name == "BASE").V.Num;
            }
        };

        var entry = Weaver.LoadDebug(Parser.Parse(Lexer.ReadProgram(sbPath)));
        var output = new StringWriter();
        Engine.PendingSink = rec;
        try
        {
            Assert.Equal(0, entry(output, TextReader.Null, Array.Empty<string>()));
        }
        finally
        {
            Engine.PendingSink = null;
        }

        Assert.Equal("89\n", output.ToString());
        Assert.Equal(new[] { "(program)", "MAIN", "SQUARE" }, frameNames);
        Assert.Equal(7, nSeen);
        Assert.Equal(49, xSeen);
        Assert.Equal(40, baseGlobal);
        Assert.Contains(rec.Hits, h => h.File == "prog.shoddy" && h.Line == 7 && h.Frame == "MAIN");
    }

    sealed class PathRecorder : IDebugSink
    {
        public readonly List<string> Paths = new();
        public void OnLine(Engine rt) => Paths.Add(rt.DebugFiles[rt.Frames[^1].FileId]);
    }

    /// <summary>An instrumented machine is woven in one place and run from
    /// another: the VS Code extension ships a library woven on the build
    /// machine, and an install puts it somewhere else. Stepping into one
    /// must show its source where it is now. Here the folder it was woven
    /// in is deleted before it runs, so a path recorded at weave time
    /// cannot be found by accident. The layout is the toolchain's: the
    /// instrumented weave two folders below the sources, in lib/debug/,
    /// as the weave records it.</summary>
    [Fact]
    public void AnInstrumentedMachineFindsItsSourceWhereverItIsInstalled()
    {
        // A name of its own: an assembly loads once per test process.
        string stem = "reloc" + Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(Path.GetTempPath(), "shoddy-reloc", Guid.NewGuid().ToString("N"));
        string wovenIn = Path.Combine(root, "woven");
        string installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(wovenIn);
        string sb = Path.Combine(wovenIn, stem + ".shoddy");
        File.WriteAllText(sb, string.Join('\n',
            "Def Twice(n As Number) As Number",
            "    n * 2",
            ""));
        var machines = new MachineSet(debug: true);
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        string dll = Weaver.WeaveMachine(Parser.Parse(lines, prog), sb,
            Path.Combine(wovenIn, "lib", "debug", MachineSet.AssemblyNameFor(sb) + ".dll"),
            machines.Machines, debug: true);

        // The install's layout, copied whole: the source, and the weave
        // in lib/debug/ two below it. Then the original goes.
        string movedSb = Path.Combine(installed, stem + ".shoddy");
        string movedDll = Path.Combine(installed, "lib", "debug", Path.GetFileName(dll));
        Directory.CreateDirectory(Path.GetDirectoryName(movedDll)!);
        File.Copy(sb, movedSb);
        File.Copy(dll, movedDll);
        Directory.Delete(wovenIn, recursive: true);

        var rec = new PathRecorder();
        Engine.PendingSink = rec;
        try
        {
            ShoddyHost host = ShoddyHost.Load(Assembly.LoadFrom(movedDll));
            Assert.Equal(14, host.Word("Twice").Call(ShoddyValue.Num(7)).AsNum());
        }
        finally
        {
            Engine.PendingSink = null;
        }

        Assert.NotEmpty(rec.Paths);
        Assert.All(rec.Paths, p =>
        {
            Assert.Equal(Path.GetFullPath(movedSb), p);
            Assert.True(File.Exists(p), $"the debugger names {p}, which does not exist");
        });
    }

    [Fact]
    public void DebugWeaveIsByteIdentical()
    {
        RunGolden("examples.shoddy", "examples.out");
        RunGolden("libtest.shoddy", "libtest.out");
        RunGolden("gradebook.shoddy", "gradebook.out", "gradebook.in");
    }

    /// <summary>The debug weave, byte-identical to the plain one.
    ///
    /// Machines are spliced here rather than resolved, which the debug
    /// adapter itself no longer does. That is deliberate: MachineTests
    /// builds its own machines into a temp workspace, and loading the
    /// repo's Shoddy.Machines.* alongside them is an assembly-name
    /// conflict in one test process. What this covers is the instrumented
    /// weave; that the adapter's pipeline enforces machine scoping is
    /// covered by MachineSurfaceTests.TheDebugPipelineEnforcesTheSameScoping.</summary>
    static void RunGolden(string program, string golden, string? stdinFile = null)
    {
        var entry = Weaver.LoadDebug(Parser.Parse(Lexer.ReadProgram(Path.Combine(Root, "tst", program))));
        using TextReader input = stdinFile != null
            ? new StreamReader(Path.Combine(Root, "tst", "golden", stdinFile))
            : TextReader.Null;
        var output = new StringWriter();
        string cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Root;
        try
        {
            Assert.Equal(0, entry(output, input, Array.Empty<string>()));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "tst", "golden", golden)),
                     output.ToString());
    }
}
