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
/// key-driven programs testable headless (feed a StringReader). One
/// deliberate exception: an ESC immediately followed by a pending 'O'
/// or '[' is assembled into the whole three-character sequence, because
/// that is exactly what the live-console path delivers for an arrow or
/// PF key and the docs promise the same string on every path — a piped
/// host enqueues "\x1bOC" atomically and the game's EvalKey must see
/// it as one keystroke, not three.
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

    [Fact]
    public void ArrowSequenceArrivesWholeUnderRedirectedInput()
    {
        // the application-mode right arrow, exactly as the console path
        // encodes it — one InKey answers the whole sequence
        string output = Run(
            "Def Main()\n    Let k = InKey()\n    If k = Chr(27) & \"OC\" Then\n        Print(\"whole\")\n    Else\n        Print(\"split\")\n",
            "\x1bOC");
        Assert.Equal("whole\n", output);
    }

    [Fact]
    public void CsiArrowFormArrivesWholeToo()
    {
        string output = Run(
            "Def Main()\n    Let k = InKey()\n    If k = Chr(27) & \"[D\" Then\n        Print(\"whole\")\n    Else\n        Print(\"split\")\n",
            "\x1b[D");
        Assert.Equal("whole\n", output);
    }

    [Fact]
    public void ABareEscapeStaysASingleKey()
    {
        string output = Run(
            "Def Main()\n    Print(Str(Len(InKey())))\n",
            "\x1b");
        Assert.Equal("1\n", output);
    }

    [Fact]
    public void EscapeBeforeAnOrdinaryCharacterSwallowsNothing()
    {
        // ESC then q is two keystrokes, not a sequence — the assembly
        // triggers only on the O / [ introducers a terminal really sends
        string output = Run(
            "Def Main()\n    Let a = InKey()\n    Let b = InKey()\n    Print(Str(Len(a)) & b)\n",
            "\x1bq");
        Assert.Equal("1q\n", output);
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
