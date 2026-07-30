// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The machines-as-DLLs path: build all nine standard machines in
/// dependency order in an isolated workspace, weave libtest against them
/// (no source splicing for the library), and demand the same golden
/// bytes. libtest never includes seq.shoddy directly — its surface must
/// arrive transitively through the matrix/dict/stats/isam machine
/// references.
/// </summary>
[Collection("golden")]
public class MachineTests
{
    static readonly string Root = RepoRoot.Dir;

    // seq and str first; the rest reference them (stats also needs dict).
    // neural is built last for compile coverage only — libtest does not
    // include it, so it never joins the resolved-machine count below.
    static readonly string[] BuildOrder =
        { "seq", "str", "matrix", "money", "file", "recio", "dict", "stats", "isam", "neural" };

    [Fact]
    public void LibTestWovenAgainstMachines()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-machines", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        Directory.CreateDirectory(Path.Combine(ws, "tst"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        File.Copy(Path.Combine(Root, "tst", "libtest.shoddy"), Path.Combine(ws, "tst", "libtest.shoddy"));

        foreach (string name in BuildOrder)
        {
            string sb = Path.Combine(ws, "machines", name + ".shoddy");
            var (prog, machines) = ParseWithMachines(sb);
            Weaver.WeaveMachine(prog, sb, machines.Machines);
        }

        var (main, set) = ParseWithMachines(Path.Combine(ws, "tst", "libtest.shoddy"));
        Assert.Equal(9, set.Machines.Count);        // all nine resolved, incl. transitive seq
        Assert.True(main.ExternalDefs.ContainsKey("SUM"));   // stats arrived via references
        Assert.True(main.ExternalDefs.ContainsKey("ANY"));   // seq arrived transitively

        string dll = Path.Combine(ws, "out", "libtest.dll");
        Directory.CreateDirectory(Path.Combine(ws, "out"));
        Weaver.Weave(main, dll, set.Machines);

        var asm = Assembly.Load(File.ReadAllBytes(dll));
        MethodInfo run = asm.GetType("Woven")!.GetMethod("Run")!;
        var output = new StringWriter();
        string cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = ws;          // libtest's file I/O stays in the workspace
        try
        {
            Assert.Equal(0, (int)run.Invoke(null, new object?[] { output, TextReader.Null, Array.Empty<string>() })!);
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "tst", "golden", "libtest.out")),
                     output.ToString());
    }

    static (ShoddyProgram, MachineSet) ParseWithMachines(string file)
    {
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(file, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        return (Parser.Parse(lines, prog), machines);
    }
}
