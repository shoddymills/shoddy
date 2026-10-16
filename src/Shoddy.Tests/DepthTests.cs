// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Hosting;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// Recursion has no practical limit. A non-tail recursion two million
/// deep returns under every launcher: `mill run`, a woven program's own
/// Main, the hosting surface's RunWovenAsync, and the perch's launch.
/// Each gives the program a thread of Engine.ProgramStackBytes; before
/// this they gave it a 1 MB thread or a thread-pool thread, and about
/// twenty thousand frames were a process kill with no line number.
///
/// Every test goes through a launcher the way a user reaches it, never
/// through Weaver.Execute on the test's own thread: the test thread's
/// stack is exactly what these must not rely on.
///
/// Golden collection: the carried mill and Engine.PendingSink are
/// shared state.
/// </summary>
[Collection("golden")]
public class DepthTests
{
    const int Depth = 2_000_000;

    static readonly string DeepSource = string.Join('\n',
        "Def Deep(n As Number) As Number",
        "    If n = 0 Then",
        "        0",
        "    Else",
        "        1 + Deep(n - 1)",
        "",
        "Def Main()",
        $"    Print(Deep({Depth}))",
        "");

    static string Expected => Depth.ToString();

    /// <summary>The mill this test host carries, as a process: nothing
    /// built or published ahead of the run.</summary>
    static (int Exit, string Out, string Err) Mill(string args, string cwd) =>
        Run("dotnet", $"\"{TestHost.MillDll}\" {args}", cwd);

    static string Workspace(string name)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-depth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, name + ".shoddy"), DeepSource);
        return ws;
    }

    static (int Exit, string Out, string Err) Run(string exe, string args, string cwd) =>
        ProcessRun.Capture(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd });

    [Fact]
    public void MillRunReturnsFromTwoMillionFrames()
    {
        string ws = Workspace("deep");
        var (exit, o, e) = Mill("run deep.shoddy", ws);
        Assert.True(exit == 0, o + e);
        Assert.Equal(Expected, o.Trim());
    }

    [Fact]
    public void AWovenProgramsOwnMainReturnsFromTwoMillionFrames()
    {
        string ws = Workspace("deep");
        var weave = Mill("weave deep.shoddy", ws);
        Assert.True(weave.Exit == 0, weave.Out + weave.Err);
        var (exit, o, e) = Run("dotnet", Path.Combine("bin", "deep.dll"), ws);
        Assert.True(exit == 0, o + e);
        Assert.Equal(Expected, o.Trim());
    }

    [Fact]
    public async Task RunWovenAsyncReturnsFromTwoMillionFrames()
    {
        // Its own assembly name: a woven dll is loaded by name into the
        // test process, and two tests must not load two files as one.
        string ws = Workspace("deephost");
        string sb = Path.Combine(ws, "deephost.shoddy");
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);
        string dll = Path.Combine(ws, "deephost.dll");
        Weaver.Weave(parsed, dll, machines.Machines);
        Assembly asm = Assembly.LoadFrom(dll);

        var output = new StringWriter();
        int rc = await ShoddyHost.RunWovenAsync(asm, output, TextReader.Null, Array.Empty<string>());
        Assert.Equal(0, rc);
        Assert.Equal(Expected, output.ToString().Trim());
    }

    [Fact]
    public void ThePerchLaunchReturnsFromTwoMillionFrames()
    {
        // The debug weave carries a frame per Def call for the stack
        // view, a try/finally round each, and a line hook per statement:
        // the heaviest frame any launcher builds, so the one most worth
        // proving at this depth. Through the launcher itself: the
        // program's own process, started as the adapter starts it.
        string ws = Workspace("deep");
        string sb = Path.Combine(ws, "deep.shoddy");
        using var s = PerchSession.Start(ws, sb);
        s.Launch(sb);
        s.ConfigurationDone();
        Assert.Equal(0, s.ExitCode());
        Assert.Contains(Expected, s.Finish());
    }
}
