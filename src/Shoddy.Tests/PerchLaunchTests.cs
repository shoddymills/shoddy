// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Text.Json;

namespace Shoddy.Tests;

/// <summary>
/// The debugger as the editor reaches it: the program's own process,
/// started as the adapter starts it (`mill run --perch PORT FILE` in the
/// program's folder, connecting back to the port), the protocol over
/// that connection, and the program on that process's console. The test
/// plays the adapter (<see cref="PerchSession"/>). Everything a program
/// can do under `mill run` it must do here: read the keyboard, draw on
/// the screen, take its arguments, read files beside it, use the network
/// when allowed, and call into machines, which the debugger steps into.
///
/// The mill is this test host's own copy of mill.dll, which carries the
/// standard library in lib/ and lib/debug/ and the sources in machines/
/// beside it, exactly as a published toolchain does. SHODDYLIB pins the
/// sources to the checkout's own machines/ so a breakpoint set in a
/// machine names the file in the tree.
///
/// Every test runs twice: once with the program in a folder inside this
/// checkout, once in a temporary folder outside it. Debugging must not
/// depend on where a program lives.
///
/// Out of process on purpose. The debugger loads the instrumented weave
/// of every machine, and those share assembly names with the release
/// weaves the rest of this test process loads.
/// </summary>
[Collection("golden")]
public class PerchLaunchTests
{
    static readonly string Root = RepoRoot.Dir;

    static string MillDll => TestHost.MillDll;

    /// <summary>A fresh folder for one program: inside the checkout
    /// (under artifacts/, which is build output) or outside it.</summary>
    static string Folder(bool inRepo)
    {
        string parent = inRepo
            ? Path.Combine(Root, "artifacts", "perch-launch")
            : Path.Combine(Path.GetTempPath(), "shoddy-perch-launch");
        string dir = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Program(string dir, string name, string source)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    // ---- the console: keyboard, screen, arguments, files ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProgramHasItsArgumentsKeyboardScreenAndFolder(bool inRepo)
    {
        string dir = Folder(inRepo);
        File.WriteAllText(Path.Combine(dir, "data.txt"), "beside the program");
        string prog = Program(dir, "console.shoddy", """
            Def Main()
                Print(Length(Args()))
                Print(ReadFile("data.txt"))
                Print(Chr(27) & "[2J")
                Let name = Input("name? ")
                Print("HELLO " & name)
            """);

        using var s = PerchSession.Start(dir, prog, stdin: "ADA\n");
        s.Launch(prog, new { args = new[] { "x", "y" } });
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        string screen = s.Finish();

        Assert.Contains("2\n", screen);
        Assert.Contains("beside the program", screen);
        Assert.Contains("\u001b[2J", screen);           // escape codes reach the screen whole
        Assert.Contains("name? HELLO ADA", screen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InKeyReadsTheKeyboard(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "keys.shoddy", """
            Def Main()
                Print("[" & InKey() & InKey() & "]")
            """);

        using var s = PerchSession.Start(dir, prog, stdin: "qz");
        s.Launch(prog);
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("[qz]", s.Finish());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SoundPlaysUnderTheDebugger(bool inRepo)
    {
        // With no audio device (a CI runner) the buzzer is silent by
        // design; what this proves is that the sound words run under the
        // debugger exactly as under `mill run`, and the program goes on.
        string dir = Folder(inRepo);
        string prog = Program(dir, "tune.shoddy", """
            Include "buzzer.shoddy"

            Def Main()
                Play(1, "T240 O4 L16 C D E")
                Print("PLAYED")
            """);
        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("PLAYED", s.Finish());
    }

    // ---- the network ------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowNetArmsTheNetwork(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "net.shoddy", """
            Def Main()
                Print(NetAllowed())
            """);

        using (var on = PerchSession.Start(dir, prog))
        {
            on.Launch(prog, new { allowNet = true });
            on.ConfigurationDone();
            Assert.Equal(0, on.ExitCode());
            Assert.Contains("True", on.Finish());
        }
        using (var off = PerchSession.Start(dir, prog))
        {
            off.Launch(prog);
            off.ConfigurationDone();
            Assert.Equal(0, off.ExitCode());
            Assert.Contains("False", off.Finish());
        }
    }

    [Fact]
    public void TheNetDemoRunsUnderTheDebugger()
    {
        string demo = Path.Combine(Root, "tst", "net-demo.shoddy");
        using var s = PerchSession.Start(Path.GetDirectoryName(demo)!, demo);
        s.Launch(demo, new { allowNet = true });
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        string screen = s.Finish();
        Assert.Contains("server said: SHODDY SPEAKS TCP", screen);
        Assert.Contains("done", screen);
    }

    // ---- pause ------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PauseStopsARunningProgram(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "spin.shoddy", """
            Def Spin(n As Number) As Number
                Spin(n + 1)

            Def Main()
                Print(Spin(0))
            """);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.ConfigurationDone();
        // Main's one line hands straight to Spin, which never returns: a
        // second in, the program is in the loop. A pause sent sooner
        // stops correctly on Main's line, which is not what this tests.
        Thread.Sleep(1000);
        s.Request("pause", new { threadId = 1 });
        using (JsonDocument stopped = s.WaitForEvent("stopped"))
            Assert.Equal("pause", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
        JsonElement top = s.TopFrame();
        Assert.Equal("SPIN", top.GetProperty("name").GetString());
        // Either of Spin's lines: the header, where each pass binds n, or
        // the body. Where in the loop a pause lands is the moment's.
        Assert.Contains(top.GetProperty("line").GetInt32(), new[] { 1, 2 });
        s.Request("disconnect");
    }

    // ---- runtime errors ---------------------------------------------------

    const string Divides = """
        Def Divide(a As Number, b As Number) As Number
            a / b

        Def Main()
            Let x = 5
            Print(Divide(x, 0))
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARuntimeErrorStopsAtTheFailingLineWithItsState(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "divide.shoddy", Divides);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Request("setExceptionBreakpoints", new { filters = new[] { "errors" } });
        s.WaitForResponse("setExceptionBreakpoints").Dispose();
        s.ConfigurationDone();

        using (JsonDocument stopped = s.WaitForEvent("stopped"))
        {
            JsonElement body = stopped.RootElement.GetProperty("body");
            Assert.Equal("exception", body.GetProperty("reason").GetString());
            Assert.Contains("division by zero", body.GetProperty("text").GetString());
        }
        JsonElement top = s.TopFrame();
        Assert.Equal("DIVIDE", top.GetProperty("name").GetString());
        Assert.Equal(2, top.GetProperty("line").GetInt32());
        Dictionary<string, string> locals = s.Locals(top.GetProperty("id").GetInt32());
        Assert.Equal("5", locals["A"]);
        Assert.Equal("0", locals["B"]);

        s.Request("exceptionInfo", new { threadId = 1 });
        using (JsonDocument info = s.WaitForResponse("exceptionInfo"))
            Assert.Contains("division by zero",
                info.RootElement.GetProperty("body").GetProperty("description").GetString());

        s.Request("continue", new { threadId = 1 });
        Assert.Equal(1, s.ExitCode());
        Assert.Contains("division by zero", s.Finish());
    }

    [Fact]
    public void WithTheFilterOffAnErrorEndsTheProgramWithoutStopping()
    {
        string dir = Folder(inRepo: false);
        string prog = Program(dir, "divide.shoddy", Divides);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Request("setExceptionBreakpoints", new { filters = Array.Empty<string>() });
        s.WaitForResponse("setExceptionBreakpoints").Dispose();
        s.ConfigurationDone();
        Assert.Equal(1, s.ExitCode());
        Assert.Contains("division by zero", s.Finish());
    }

    // ---- machines ---------------------------------------------------------

    const string UsesSeq = """
        Include "seq.shoddy"

        Def Main()
            Print(IndexOf({ 10, 20, 30 }, 20))
        """;

    static string SeqSource => Path.Combine(Root, "machines", "seq.shoddy");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABreakpointInAMachineBindsAndStops(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "useseq.shoddy", UsesSeq);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        // seq.shoddy:193 is IndexOf's body.
        s.Request("setBreakpoints", new
        {
            source = new { path = SeqSource },
            breakpoints = new[] { new { line = 193 } },
        });
        using (JsonDocument bp = s.WaitForResponse("setBreakpoints"))
            Assert.True(bp.RootElement.GetProperty("body").GetProperty("breakpoints")[0]
                .GetProperty("verified").GetBoolean());
        s.ConfigurationDone();

        using (JsonDocument stopped = s.WaitForEvent("stopped"))
            Assert.Equal("breakpoint", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
        JsonElement top = s.TopFrame();
        Assert.Equal("INDEXOF", top.GetProperty("name").GetString());
        Assert.Equal(193, top.GetProperty("line").GetInt32());
        Assert.Equal(Path.GetFullPath(SeqSource),
            top.GetProperty("source").GetProperty("path").GetString(),
            StringComparer.OrdinalIgnoreCase);

        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("2", s.Finish());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StepInGoesIntoAMachine(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "useseq.shoddy", UsesSeq);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog, new { stopOnEntry = true });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();                // entry: Main's first line
        Assert.Equal("MAIN", s.TopFrame().GetProperty("name").GetString());

        s.Request("stepIn", new { threadId = 1 });
        s.WaitForEvent("stopped").Dispose();
        JsonElement top = s.TopFrame();
        Assert.Equal("INDEXOF", top.GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(SeqSource),
            top.GetProperty("source").GetProperty("path").GetString(),
            StringComparer.OrdinalIgnoreCase);

        s.Request("disconnect");
    }

    /// <summary>The VS Code extension ships a library woven on the build
    /// machine, so the library a user debugs against is never the folder
    /// it was woven in. Here seq is woven in one folder, installed in
    /// another, and the first is deleted: a breakpoint set in the installed
    /// seq.shoddy must stop, and the frame must name that file, which must
    /// exist. Nothing may be rewoven on the way: the resolver builds
    /// nothing, so a machine missing from the installed library is an
    /// error, never a weave made where it runs.</summary>
    [Fact]
    public void AMachineDebugsWhereItIsInstalledNotWhereItWasWoven()
    {
        string root = Path.Combine(Path.GetTempPath(), "shoddy-installed", Guid.NewGuid().ToString("N"));
        string woven = Path.Combine(root, "woven", "machines");
        string installed = Path.Combine(root, "installed", "machines");
        Directory.CreateDirectory(woven);
        File.Copy(SeqSource, Path.Combine(woven, "seq.shoddy"));
        // The instrumented weave, to lib/debug/ two folders below the
        // source: the layout a toolchain carries, and the one the weave
        // records its source path against.
        var (exit, said) = ProcessRun.CaptureAll(
            new ProcessStartInfo("dotnet", $"\"{MillDll}\" machine --debug seq.shoddy -o lib/debug")
            { WorkingDirectory = woven });
        Assert.True(exit == 0, said);

        string dllName = Path.Combine("lib", "debug", "Shoddy.Machines.Seq.dll");
        Directory.CreateDirectory(Path.Combine(installed, "lib", "debug"));
        string seq = Path.Combine(installed, "seq.shoddy");
        string dll = Path.Combine(installed, dllName);
        File.Copy(Path.Combine(woven, "seq.shoddy"), seq);
        File.Copy(Path.Combine(woven, dllName), dll);
        Directory.Delete(Path.Combine(root, "woven"), recursive: true);

        string dir = Folder(inRepo: false);
        string prog = Program(dir, "useseq.shoddy", UsesSeq);
        // The installed library, sources and compiled form alike: the
        // two variables a pinned library is named by.
        using var s = PerchSession.Start(dir, prog, env: new Dictionary<string, string>
        {
            ["SHODDYLIB"] = installed,
            ["SHODDYLIBBIN"] = Path.Combine(installed, "lib"),
        });
        s.Launch(prog);
        s.Breakpoints(seq, new { line = 193 });             // IndexOf's body
        s.ConfigurationDone();

        using (JsonDocument stopped = s.WaitForEvent("stopped"))
            Assert.Equal("breakpoint", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
        JsonElement top = s.TopFrame();
        Assert.Equal("INDEXOF", top.GetProperty("name").GetString());
        string shown = top.GetProperty("source").GetProperty("path").GetString()!;
        Assert.Equal(Path.GetFullPath(seq), shown, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(shown), $"the debugger names {shown}, which does not exist");

        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        s.Finish();
    }

    // ---- stepping ---------------------------------------------------------

    const string TwoLines = """
        Include "seq.shoddy"

        Def Main()
            Let i = IndexOf({ 10, 20, 30 }, 20)
            Print(i)
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StepOverStaysInTheCallAndMovesToTheNextLine(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "two.shoddy", TwoLines);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog, new { stopOnEntry = true });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();
        Assert.Equal(4, s.TopFrame().GetProperty("line").GetInt32());

        s.Request("next", new { threadId = 1 });
        s.WaitForEvent("stopped").Dispose();
        JsonElement top = s.TopFrame();
        Assert.Equal("MAIN", top.GetProperty("name").GetString());
        Assert.Equal(5, top.GetProperty("line").GetInt32());
        s.Request("disconnect");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StepOutOfAMachineReturnsToTheCaller(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "two.shoddy", TwoLines);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog, new { stopOnEntry = true });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();
        s.Request("stepIn", new { threadId = 1 });
        s.WaitForEvent("stopped").Dispose();
        Assert.Equal("INDEXOF", s.TopFrame().GetProperty("name").GetString());

        s.Request("stepOut", new { threadId = 1 });
        s.WaitForEvent("stopped").Dispose();
        JsonElement top = s.TopFrame();
        Assert.Equal("MAIN", top.GetProperty("name").GetString());
        Assert.Equal(5, top.GetProperty("line").GetInt32());
        s.Request("disconnect");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABreakpointStopsOncePerExecutionOfItsLine(bool inRepo)
    {
        // Line 4 opens a block of its own (the list literal), which hooks
        // the line a second time. Continue must not stop there again.
        string dir = Folder(inRepo);
        string prog = Program(dir, "two.shoddy", TwoLines);

        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Request("setBreakpoints", new
        {
            source = new { path = prog },
            breakpoints = new[] { new { line = 4 } },
        });
        s.WaitForResponse("setBreakpoints").Dispose();
        s.ConfigurationDone();
        using (JsonDocument stopped = s.WaitForEvent("stopped"))
            Assert.Equal("breakpoint", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());

        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("2", s.Finish());
    }

    // ---- expressions and variables -----------------------------------------

    const string Points = """
        Include "seq.shoddy"

        Type Point
            Px As Number
            Py As Number

        Let scale = 10

        Def Twice(n As Number) As Number
            n * 2

        Def Main()
            Let p = Point(3, 4)
            Let k = 7
            Print(Twice(k) + Px(p) + scale)
        """;

    /// <summary>Launch Points stopped at line 15, the Print.</summary>
    static PerchSession AtThePrint(string dir, out string prog)
    {
        prog = Program(dir, "points.shoddy", Points);
        var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Breakpoints(prog, new { line = 15 });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();
        return s;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDebugConsoleEvaluatesAnyExpression(bool inRepo)
    {
        using PerchSession s = AtThePrint(Folder(inRepo), out _);
        int frame = s.TopFrame().GetProperty("id").GetInt32();

        Assert.Equal("8", s.Evaluate(frame, "k + 1"));                    // a local
        Assert.Equal("20", s.Evaluate(frame, "scale * 2"));               // a top-level Let
        Assert.Equal("3", s.Evaluate(frame, "Px(p)"));                    // a field of a record
        Assert.Equal("14", s.Evaluate(frame, "Twice(k)"));                // the program's own Def
        Assert.Equal("2", s.Evaluate(frame, "IndexOf({ 5, 6 }, 6)"));     // a machine word
        Assert.Contains("Point", s.Evaluate(frame, "Point(1, 2)"));       // the program's own type
        Assert.Null(s.Evaluate(frame, "NoSuchWord(k)"));                  // refused, not guessed

        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("27", s.Finish());                                 // the program is untouched
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingAVariableChangesWhatTheProgramComputes(bool inRepo)
    {
        using PerchSession s = AtThePrint(Folder(inRepo), out _);
        int frame = s.TopFrame().GetProperty("id").GetInt32();
        int[] scopes = s.Scopes(frame);                   // Locals, Globals, Value Stack

        Assert.Equal("100", s.SetVariable(scopes[0], "K", "50 * 2"));
        Assert.Equal("0", s.SetVariable(scopes[1], "SCALE", "0"));
        int point = s.Variables(scopes[0])["P"].Reference;
        Assert.Equal("30", s.SetVariable(point, "Px", "30"));
        Assert.Equal("30", s.Evaluate(frame, "Px(p)"));

        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("230", s.Finish());                                // 200 + 30 + 0
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABreakpointOnATopLevelLetStops(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "points.shoddy", Points);
        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Breakpoints(prog, new { line = 7 });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();
        JsonElement top = s.TopFrame();
        Assert.Equal("(program)", top.GetProperty("name").GetString());
        Assert.Equal(7, top.GetProperty("line").GetInt32());
        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("27", s.Finish());
    }

    // ---- breakpoint conditions, hit counts, logpoints -----------------------

    const string Counts = """
        Def Count(n As Number) As Number
            If n = 0 Then
                0
            Else
                n + Count(n - 1)

        Def Main()
            Print(Count(5))
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AConditionalBreakpointStopsOnlyWhenItsConditionHolds(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "count.shoddy", Counts);
        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Breakpoints(prog, new { line = 2, condition = "n = 3" });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();
        Assert.Equal("3", s.Locals(s.TopFrame().GetProperty("id").GetInt32())["N"]);
        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());                    // no second stop
        Assert.Contains("15", s.Finish());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHitCountStopsOnThatHit(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "count.shoddy", Counts);
        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Breakpoints(prog, new { line = 2, hitCondition = "4" });
        s.ConfigurationDone();
        s.WaitForEvent("stopped").Dispose();
        // n = 5, 4, 3, 2: the fourth hit
        Assert.Equal("2", s.Locals(s.TopFrame().GetProperty("id").GetInt32())["N"]);
        s.Request("continue", new { threadId = 1 });
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("15", s.Finish());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALogpointLogsEveryHitAndNeverStops(bool inRepo)
    {
        string dir = Folder(inRepo);
        string prog = Program(dir, "count.shoddy", Counts);
        using var s = PerchSession.Start(dir, prog);
        s.Launch(prog);
        s.Breakpoints(prog, new { line = 2, logMessage = "n is {n}, doubled {n * 2}" });
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("15", s.Finish());
        string console = s.Console();
        for (int n = 5; n >= 0; n--)
            Assert.Contains($"n is {n}, doubled {n * 2}\n", console);
    }
}
