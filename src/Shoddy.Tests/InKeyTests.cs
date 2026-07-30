// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The INKEY builtin on its redirected-input path: when the engine's
/// reader is not the live console, InKey consumes one pending character
/// per call and reports "" at end of input — which is what makes
/// key-driven programs testable headless (feed a StringReader). The
/// live-console path (Console.KeyAvailable / ReadKey, arrows encoded as
/// VT100 application-mode sequences) needs a real keyboard and is left
/// to manual play.
/// </summary>
public class InKeyTests
{
    [Fact]
    public void ConsumesOneCharPerCallThenReportsEmpty()
    {
        // three calls against two fed characters: "a", "b", then ""
        string output = Run(
            "Def Main()\n    Print(InKey() & InKey() & \"|\" & InKey() & \"|\")\n",
            "ab");
        Assert.Equal("ab||\n", output);
    }

    [Fact]
    public void ReportsEmptyAtEndOfInputWithoutBlocking()
    {
        string output = Run(
            "Def Main()\n    Print(\"got[\" & InKey() & \"]\")\n",
            "");
        Assert.Equal("got[]\n", output);
    }

    // ---- helper --------------------------------------------------------

    static string Run(string src, string feed)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-inkey", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        string f = Path.Combine(ws, "prog.shoddy");
        File.WriteAllText(f, src);

        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(f, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Parser.Parse(lines, prog);
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, machines.Machines, output, new StringReader(feed), Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }
}
