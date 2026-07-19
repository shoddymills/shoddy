using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The machines-as-DLLs path: build all eight standard machines in
/// dependency order in an isolated workspace, weave libtest against them
/// (no source splicing for the library), and demand the same golden
/// bytes. libtest never includes seq.shoddy directly — its surface must
/// arrive transitively through the matrix/dict/isam machine references.
/// </summary>
[Collection("golden")]
public class MachineTests
{
    static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    // seq and str first; the rest reference them
    static readonly string[] BuildOrder =
        { "seq", "str", "matrix", "money", "file", "recio", "dict", "isam" };

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
        Assert.Equal(8, set.Machines.Count);        // all eight resolved, incl. transitive seq
        Assert.True(main.ExternalDefs.ContainsKey("SUM"));   // seq arrived via references

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
