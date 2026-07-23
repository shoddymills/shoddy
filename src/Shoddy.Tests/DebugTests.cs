// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
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
            Assert.Equal(0, entry(output, TextReader.Null));
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

    [Fact]
    public void DebugWeaveIsByteIdentical()
    {
        RunGolden("examples.shoddy", "examples.out");
        RunGolden("libtest.shoddy", "libtest.out");
        RunGolden("gradebook.shoddy", "gradebook.out", "gradebook.in");
    }

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
            Assert.Equal(0, entry(output, input));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "tst", "golden", golden)),
                     output.ToString());
    }
}
