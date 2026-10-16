// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The machines-as-a-library path: weave every standard machine into one
/// assembly in an isolated workspace, resolve libtest against it (no
/// source splicing for the library), and demand the same golden bytes.
///
/// libtest includes seq.shoddy itself. It used to reach seq's surface
/// transitively through matrix/dict/stats/isam, which is exactly what a
/// machine's surface being scoped to its own declarations ended: a
/// consumer names what it uses.
///
/// The library is woven here, in process, from a copy of the sources:
/// the generator orders the machines by their includes, seeds each from
/// the ones ahead of it, and one compilation holds them all, which is
/// what the library project does with the C# compiler. The assembly
/// carries the standard library's own name so the weave is the real
/// thing; nothing else in this process loads that name, because a set
/// searches only the libraries it was given.
/// </summary>
[Collection("golden")]
public class MachineTests
{
    static readonly string Root = RepoRoot.Dir;

    [Fact]
    public void LibTestWovenAgainstTheLibrary()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-machines", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        Directory.CreateDirectory(Path.Combine(ws, "tst"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        File.Copy(Path.Combine(Root, "tst", "libtest.shoddy"), Path.Combine(ws, "tst", "libtest.shoddy"));

        // Every machine, one assembly, in dependency order.
        List<string> sources = LibraryGen.SourcesIn(new[] { Path.Combine(ws, "machines") });
        string library = Weaver.WeaveLibrary(sources, Path.Combine(ws, "lib", "Shoddy.Machines.dll"), "Shoddy.Machines");
        Assert.Equal(sources.Count, MachineInfo.AllIn(Assembly.LoadFrom(library), library).Count);

        var (main, set) = ParseWithMachines(Path.Combine(ws, "tst", "libtest.shoddy"), library);
        // Every machine libtest reaches, by name or behind a name: the
        // whole closure resolves, nothing splices.
        Assert.Superset(new HashSet<string> { "seq", "str", "matrix", "money", "file", "recio",
                                              "dict", "stats", "isam", "json", "xml", "html" },
                        set.Machines.Select(m => Path.GetFileNameWithoutExtension(m.Source)).ToHashSet());
        Assert.True(main.ExternalDefs.ContainsKey("SUM"));   // stats arrived via references
        Assert.True(main.ExternalDefs.ContainsKey("ANY"));   // seq, included by name
        Assert.True(main.ExternalDefs.ContainsKey("JSONTEXT"));   // json arrived too
        Assert.True(main.ExternalDefs.ContainsKey("XMLTEXT"));    // and xml
        Assert.True(main.ExternalDefs.ContainsKey("HTMLTEXT"));   // and html on top of it
        // One assembly behind all of them.
        Assert.Equal(library, Assert.Single(Weaver.LibrariesOf(set.Machines)));

        string dll = Path.Combine(ws, "out", "libtest.dll");
        Weaver.Weave(main, dll, set.Machines);
        // The woven program carries the one library beside it, not a DLL
        // per machine.
        Assert.True(File.Exists(Path.Combine(ws, "out", "Shoddy.Machines.dll")));
        Assert.Single(Directory.GetFiles(Path.Combine(ws, "out"), "Shoddy.Machines*.dll"));

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

    /// <summary>A library's generator refuses a cycle by name rather
    /// than looping, and names an include it cannot place.</summary>
    [Fact]
    public void TheGeneratorRefusesACycleAndAnUnplacedInclude()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-machines", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cyca.shoddy"), "Include \"cycb.shoddy\"\n\nDef CycA() As Number\n    1\n");
        File.WriteAllText(Path.Combine(dir, "cycb.shoddy"), "Include \"cyca.shoddy\"\n\nDef CycB() As Number\n    2\n");
        var cycle = Assert.Throws<ShoddyError>(() =>
            LibraryGen.Generate(LibraryGen.SourcesIn(new[] { dir }), false, null, "."));
        Assert.Contains("cycle", cycle.Message);
        Assert.Contains("cyca", cycle.Message);

        string lone = Path.Combine(Path.GetTempPath(), "shoddy-machines", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lone);
        File.WriteAllText(Path.Combine(lone, "needs.shoddy"), "Include \"absent.shoddy\"\n\nDef Needs() As Number\n    1\n");
        var missing = Assert.Throws<ShoddyError>(() =>
            LibraryGen.Generate(LibraryGen.SourcesIn(new[] { lone }), false, null, "."));
        Assert.Contains("absent", missing.Message);
    }

    static (ShoddyProgram, MachineSet) ParseWithMachines(string file, string library)
    {
        var machines = new MachineSet(false, new[] { library });
        var lines = Lexer.ReadProgram(file, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        return (Parser.Parse(lines, prog), machines);
    }
}
