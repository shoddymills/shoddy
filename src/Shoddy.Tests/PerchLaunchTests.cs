// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Shoddy.Tests;

/// <summary>
/// The debugger as the editor reaches it: the published mill, started as
/// `mill dap --listen PORT` in the program's own folder, the protocol over
/// loopback TCP, and the program on that process's console. Everything a
/// program can do under `mill run` it must do here: read the keyboard,
/// draw on the screen, take its arguments, read files beside it, use the
/// network when allowed, and call into machines, which the debugger steps
/// into.
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

    static PerchLaunchTests()
    {
        if (!File.Exists(MillPath))
            ProcessRun.CaptureAll(new ProcessStartInfo("dotnet", "publish src/Shoddy.Mill -c Release -o bin")
            { WorkingDirectory = Root });
    }

    static string MillPath => Path.Combine(Root, "bin",
        OperatingSystem.IsWindows() ? "mill.exe" : "mill");

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

        using var s = Session.Start(dir, stdin: "ADA\n");
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

        using var s = Session.Start(dir, stdin: "qz");
        s.Launch(prog);
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("[qz]", s.Finish());
    }

    [Fact]
    public void AConnectionThatSaysNothingIsNotTheSession()
    {
        // The editor checks the port is up before it connects for real;
        // that check must not become the session.
        string dir = Folder(inRepo: false);
        string prog = Program(dir, "hello.shoddy", """
            Def Main()
                Print("HELLO")
            """);
        using var s = Session.Start(dir, probeFirst: true);
        s.Launch(prog);
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("HELLO", s.Finish());
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
        using var s = Session.Start(dir);
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

        using (var on = Session.Start(dir))
        {
            on.Launch(prog, new { allowNet = true });
            on.ConfigurationDone();
            Assert.Equal(0, on.ExitCode());
            Assert.Contains("True", on.Finish());
        }
        using (var off = Session.Start(dir))
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
        using var s = Session.Start(Path.GetDirectoryName(demo)!);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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

        using var s = Session.Start(dir);
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
    static Session AtThePrint(string dir, out string prog)
    {
        prog = Program(dir, "points.shoddy", Points);
        var s = Session.Start(dir);
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
        using Session s = AtThePrint(Folder(inRepo), out _);
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
        using Session s = AtThePrint(Folder(inRepo), out _);
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
        using var s = Session.Start(dir);
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
        using var s = Session.Start(dir);
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
        using var s = Session.Start(dir);
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
        using var s = Session.Start(dir);
        s.Launch(prog);
        s.Breakpoints(prog, new { line = 2, logMessage = "n is {n}, doubled {n * 2}" });
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains("15", s.Finish());
        string console = s.Console();
        for (int n = 5; n >= 0; n--)
            Assert.Contains($"n is {n}, doubled {n * 2}\n", console);
    }

    // ---- the session ------------------------------------------------------

    /// <summary>One `mill dap --listen` process and the protocol client
    /// talking to it. The process's stdout and stderr are its screen.</summary>
    sealed class Session : IDisposable
    {
        readonly Process proc;
        readonly TcpClient tcp;
        readonly NetworkStream stream;
        readonly StringBuilder screen = new();
        readonly StringBuilder console = new();     // Debug Console output events
        readonly Task outPump, errPump;
        int seq;
        int? exitCode;

        Session(Process proc, TcpClient tcp)
        {
            this.proc = proc;
            this.tcp = tcp;
            stream = tcp.GetStream();
            stream.ReadTimeout = 120_000;       // a hang fails rather than wedges
            outPump = Pump(proc.StandardOutput);
            errPump = Pump(proc.StandardError);
        }

        Task Pump(StreamReader r) => Task.Run(async () =>
        {
            var buf = new char[4096];
            int n;
            while ((n = await r.ReadAsync(buf)) > 0)
                lock (screen) screen.Append(buf, 0, n);
        });

        public static Session Start(string cwd, string? stdin = null, bool probeFirst = false)
        {
            int port = FreePort();
            var psi = new ProcessStartInfo(MillPath, $"dap --listen {port}")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process p = Process.Start(psi)!;
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();

            var deadline = DateTime.UtcNow.AddSeconds(60);
            if (probeFirst)
                while (true)
                {
                    using var probe = new TcpClient();
                    try { probe.Connect(IPAddress.Loopback, port); break; }
                    catch (SocketException) when (DateTime.UtcNow < deadline && !p.HasExited)
                    {
                        Thread.Sleep(100);
                    }
                }
            while (true)
            {
                var c = new TcpClient();
                try
                {
                    c.Connect(IPAddress.Loopback, port);
                    var s = new Session(p, c);
                    s.Request("initialize", new { adapterID = "shoddy" });
                    s.WaitForEvent("initialized").Dispose();
                    return s;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline && !p.HasExited)
                {
                    c.Dispose();
                    Thread.Sleep(100);
                }
            }
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Launch(string program, object? extra = null)
        {
            var args = new Dictionary<string, object?> { ["program"] = program };
            if (extra != null)
                foreach (var p in extra.GetType().GetProperties())
                    args[p.Name] = p.GetValue(extra);
            Request("launch", args);
            using JsonDocument launched = WaitForResponse("launch");
            Assert.True(launched.RootElement.GetProperty("success").GetBoolean(),
                launched.RootElement.ToString());
        }

        /// <summary>Replace the breakpoints in one file.</summary>
        public void Breakpoints(string file, params object[] breakpoints)
        {
            Request("setBreakpoints", new { source = new { path = file }, breakpoints });
            using JsonDocument r = WaitForResponse("setBreakpoints");
            foreach (JsonElement b in r.RootElement.GetProperty("body").GetProperty("breakpoints").EnumerateArray())
                Assert.True(b.GetProperty("verified").GetBoolean(), r.RootElement.ToString());
        }

        /// <summary>An expression's result in a frame, or null when the
        /// debugger refused it.</summary>
        public string? Evaluate(int frameId, string expression)
        {
            Request("evaluate", new { expression, frameId, context = "repl" });
            using JsonDocument r = WaitForResponse("evaluate");
            return r.RootElement.GetProperty("success").GetBoolean()
                ? r.RootElement.GetProperty("body").GetProperty("result").GetString()
                : null;
        }

        /// <summary>A frame's scopes' references: Locals, Globals, Value Stack.</summary>
        public int[] Scopes(int frameId)
        {
            Request("scopes", new { frameId });
            using JsonDocument sc = WaitForResponse("scopes");
            return sc.RootElement.GetProperty("body").GetProperty("scopes").EnumerateArray()
                .Select(x => x.GetProperty("variablesReference").GetInt32()).ToArray();
        }

        public Dictionary<string, (string Value, int Reference)> Variables(int reference)
        {
            Request("variables", new { variablesReference = reference });
            using JsonDocument vs = WaitForResponse("variables");
            return vs.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
                .ToDictionary(v => v.GetProperty("name").GetString()!,
                              v => (v.GetProperty("value").GetString()!, v.GetProperty("variablesReference").GetInt32()));
        }

        /// <summary>Set a variable; answers its new value as shown.</summary>
        public string SetVariable(int reference, string name, string value)
        {
            Request("setVariable", new { variablesReference = reference, name, value });
            using JsonDocument r = WaitForResponse("setVariable");
            Assert.True(r.RootElement.GetProperty("success").GetBoolean(), r.RootElement.ToString());
            return r.RootElement.GetProperty("body").GetProperty("value").GetString()!;
        }

        /// <summary>What the debugger has written to the Debug Console.</summary>
        public string Console() => console.ToString();

        public void ConfigurationDone()
        {
            Request("configurationDone");
            WaitForResponse("configurationDone").Dispose();
        }

        public void Request(string command, object? args = null)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new { seq = ++seq, type = "request", command, arguments = args });
            byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            stream.Write(head);
            stream.Write(body);
            stream.Flush();
        }

        public JsonDocument WaitForEvent(string name) => WaitFor(m =>
            m.GetProperty("type").GetString() == "event" && m.GetProperty("event").GetString() == name);

        public JsonDocument WaitForResponse(string command) => WaitFor(m =>
            m.GetProperty("type").GetString() == "response" && m.GetProperty("command").GetString() == command);

        /// <summary>The exit code the session reports for the program.</summary>
        public int ExitCode()
        {
            if (exitCode is int known) return known;
            using JsonDocument exited = WaitForEvent("exited");
            exitCode = exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32();
            return exitCode.Value;
        }

        /// <summary>The innermost frame of the stopped program.</summary>
        public JsonElement TopFrame()
        {
            Request("stackTrace", new { threadId = 1 });
            using JsonDocument st = WaitForResponse("stackTrace");
            return st.RootElement.GetProperty("body").GetProperty("stackFrames")[0].Clone();
        }

        /// <summary>A frame's Locals scope, name to displayed value.</summary>
        public Dictionary<string, string> Locals(int frameId)
        {
            Request("scopes", new { frameId });
            int reference;
            using (JsonDocument sc = WaitForResponse("scopes"))
                reference = sc.RootElement.GetProperty("body").GetProperty("scopes")[0]
                    .GetProperty("variablesReference").GetInt32();
            Request("variables", new { variablesReference = reference });
            using JsonDocument vs = WaitForResponse("variables");
            return vs.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
                .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString()!);
        }

        /// <summary>End the session and answer everything the program put
        /// on its screen.</summary>
        public string Finish()
        {
            Request("disconnect");
            if (!proc.WaitForExit(60_000)) proc.Kill(entireProcessTree: true);
            Task.WaitAll(outPump, errPump);
            lock (screen) return screen.ToString().Replace("\r\n", "\n");
        }

        JsonDocument WaitFor(Func<JsonElement, bool> want)
        {
            for (int i = 0; i < 500; i++)
            {
                JsonDocument msg = Read();
                JsonElement m = msg.RootElement;
                if (m.GetProperty("type").GetString() == "event" &&
                    m.GetProperty("event").GetString() == "exited")
                    exitCode = m.GetProperty("body").GetProperty("exitCode").GetInt32();
                if (m.GetProperty("type").GetString() == "event" &&
                    m.GetProperty("event").GetString() == "output" &&
                    m.GetProperty("body").GetProperty("category").GetString() == "console")
                    console.Append(m.GetProperty("body").GetProperty("output").GetString());
                if (want(m)) return msg;
                msg.Dispose();
            }
            throw new TimeoutException("expected message never arrived");
        }

        JsonDocument Read()
        {
            int len = -1;
            var line = new StringBuilder();
            while (true)
            {
                int c = stream.ReadByte();
                if (c < 0) throw new IOException("transport closed: " + Screen());
                if (c == '\n')
                {
                    string h = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (h.Length == 0) break;
                    if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        len = int.Parse(h["Content-Length:".Length..].Trim());
                }
                else line.Append((char)c);
            }
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = stream.Read(buf, got, len - got);
                if (n <= 0) throw new IOException("transport closed: " + Screen());
                got += n;
            }
            return JsonDocument.Parse(buf);
        }

        string Screen()
        {
            lock (screen) return screen.ToString();
        }

        public void Dispose()
        {
            tcp.Dispose();
            if (!proc.HasExited)
            {
                if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);
            }
            proc.Dispose();
        }
    }
}
