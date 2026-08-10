// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Hosting;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// Shoddy.Hosting: the Mode N surface (manifest-driven word lookup and
/// Call marshalling), the Mode T surface (RunWovenAsync over pipes), and
/// the `net` arming rules D17 promises — per-engine for Mode N behind
/// the construction lock, in both directions.
///
/// Each fixture weaves its machines fresh into its own workspace with
/// unique names, since assemblies resolve by simple name process-wide.
/// </summary>
[Collection("golden")]
public class HostingTests
{
    const string CoreSource = """
        Type HostBox
            Held As Number

        Def HbTwice(n As Number) As Number
            n * 2

        Def HbJoin(parts As List Of String, sep As String) As String
            Fold(Rest(parts), First(parts), Fn(acc, p) => acc & sep & p)

        Def HbBox(n As Number) As HostBox
            HostBox(n * 10)

        Def HbHeld(b As HostBox) As Number
            Held(b)

        Def HbNetOk() As Boolean
            NetAllowed()

        Def HbSum(xs As List Of Number) As Number
            Fold(xs, 0, +)
        """;

    static string Workspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-hosting", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        return ws;
    }

    static Assembly Machine(string name)
    {
        string ws = Workspace();
        string sb = Path.Combine(ws, name + ".shoddy");
        File.WriteAllText(sb, CoreSource);
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sb, machines.Machines);
        return Assembly.LoadFrom(MachineSet.DllPathFor(sb));
    }

    [Fact]
    public void WordsAnswerWithHandBuiltValues()
    {
        ShoddyHost host = ShoddyHost.Load(Machine("hosta"));

        Assert.Equal(42, host.Word("HbTwice").Call(ShoddyValue.Num(21)).AsNum());

        ShoddyValue joined = host.Word("HBJOIN").Call(   // case-folded lookup
            ShoddyValue.ListOf(new[] { ShoddyValue.Str("A"), ShoddyValue.Str("B") }),
            ShoddyValue.Str("-"));
        Assert.Equal("A-B", joined.AsStr());

        Assert.Equal(6, host.Word("HbSum").Call(
            ShoddyValue.ListOf(new[] { 1.0, 2.0, 3.0 }.Select(ShoddyValue.Num))).AsNum());

        Assert.Equal((1, 1), host.Word("HbTwice").Effect);
    }

    [Fact]
    public void RecordsRoundTrip()
    {
        ShoddyHost host = ShoddyHost.Load(Machine("hostb"));

        ShoddyValue box = host.Word("HbBox").Call(ShoddyValue.Num(7));
        Assert.Equal("HOSTBOX", box.TypeName());
        Assert.Equal(70, box.Field("Held").AsNum());

        // Back across the boundary: the record a word answered is a
        // valid argument to the next, identity intact.
        Assert.Equal(70, host.Word("HbHeld").Call(box).AsNum());
    }

    [Fact]
    public void CallRefusesTheWrongShape()
    {
        ShoddyHost host = ShoddyHost.Load(Machine("hostc"));
        Assert.Throws<ArgumentException>(
            () => host.Word("HbTwice").Call());                      // arity
        Assert.Throws<KeyNotFoundException>(() => host.Word("HbNothing"));
        Assert.Throws<InvalidOperationException>(
            () => host.Word("HbTwice").Call(ShoddyValue.Num(1)).AsStr());   // type
    }

    /// <summary>D17: arming is per-engine for Mode N, in both
    /// directions — an ungranted host in an armed process stays
    /// unarmed, and a granted one in a clean process arms.</summary>
    [Fact]
    public void NetArmsPerEngineBothDirections()
    {
        Assembly machine = Machine("hostd");
        string? saved = Environment.GetEnvironmentVariable("SHODDY_ALLOW_NET");
        try
        {
            Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", null);
            ShoddyHost granted = ShoddyHost.Load(
                new ShoddyHostOptions { AllowNet = true }, machine);
            ShoddyHost ungranted = ShoddyHost.Load(machine);
            Assert.True(granted.Word("HbNetOk").Call().AsBool());
            Assert.False(ungranted.Word("HbNetOk").Call().AsBool());

            // The process armed for a Mode T mill (R2's resolution) must
            // not leak into an ungranted Mode N engine.
            ShoddyHost.ArmNetForProcess();
            ShoddyHost stillUngranted = ShoddyHost.Load(machine);
            Assert.False(stillUngranted.Word("HbNetOk").Call().AsBool());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", saved);
        }
    }

    [Fact]
    public void MissingFileRootFailsFastAtLoad()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ShoddyHost.Load(
            new ShoddyHostOptions { FileRoot = Path.Combine(Workspace(), "absent") },
            Machine("hoste")));
    }

    [Fact]
    public async Task RunWovenAsyncPipesAConsoleMill()
    {
        string ws = Workspace();
        string sb = Path.Combine(ws, "hostrun.shoddy");
        File.WriteAllText(sb,
            "Def Main()\n    Let name = Input(\"WHO \")\n    Print(\"HI \" & name)\n");
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);
        string dll = Path.Combine(ws, "hostrun.dll");
        Weaver.Weave(parsed, dll, machines.Machines);

        var output = new StringWriter();
        int exit = await ShoddyHost.RunWovenAsync(Assembly.LoadFrom(dll),
            output, new StringReader("ADA\n"), Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("HI ADA", output.ToString());
    }
}
