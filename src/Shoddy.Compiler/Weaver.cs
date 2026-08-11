// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// The one and only execution path: Shoddy compiles to C# (CodeGen),
/// Roslyn compiles that against the running framework, and the result
/// either runs immediately in-process (`mill run` — compiled to memory,
/// never touching disk) or is written out as a runnable assembly
/// (`mill weave`), with a runtimeconfig.json and Shoddy.Runtime.dll (and
/// any referenced machine DLLs) alongside so `dotnet program.dll` just
/// works. Machines compile the same way, minus the entry point, plus
/// their manifest.
/// </summary>
public static class Weaver
{
    public static string GenerateSource(ShoddyProgram prog, string? machineClass = null,
                                        bool debug = false,
                                        IReadOnlyList<MachineInfo>? deps = null,
                                        string? ownFile = null) =>
        new CodeGen(prog, machineClass, debug, deps, ownFile).Generate();

    /// <summary>Compile a debug-instrumented (perch) build to memory and
    /// return its Run entry point. Install the sink on
    /// <see cref="Engine.PendingSink"/> before invoking. The perch always
    /// splices sources (no machine DLLs) so every line is steppable.</summary>
    public static Func<TextWriter, TextReader, int> LoadDebug(
        ShoddyProgram prog, IReadOnlyList<MachineInfo>? machines = null)
    {
        using var pe = new MemoryStream();
        Compile(GenerateSource(prog, debug: true, deps: machines),
                pe, OutputKind.ConsoleApplication, machines,
                "perch-" + Guid.NewGuid().ToString("N"));
        pe.Position = 0;
        Assembly asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(pe);
        MethodInfo run = asm.GetType("Woven")!.GetMethod("Run")!;
        return (o, i) => (int)run.Invoke(null, new object?[] { o, i, Array.Empty<string>() })!;
    }

    /// <summary>Compile to memory and run immediately: the `mill run`
    /// path. Returns the program's exit code.</summary>
    public static int Execute(ShoddyProgram prog, IReadOnlyList<MachineInfo>? machines,
                              TextWriter output, TextReader input, string[]? args = null)
    {
        using var pe = new MemoryStream();
        // Unique name: repeated in-process runs must not collide.
        Compile(GenerateSource(prog), pe, OutputKind.ConsoleApplication, machines,
                "woven-" + Guid.NewGuid().ToString("N"));
        pe.Position = 0;
        // Machines were Assembly.LoadFrom'd while reading manifests, which
        // lands them in the default context — references resolve by name.
        Assembly asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(pe);
        MethodInfo run = asm.GetType("Woven")!.GetMethod("Run")!;
        return (int)run.Invoke(null, new object?[] { output, input, args ?? Array.Empty<string>() })!;
    }

    public static void Weave(ShoddyProgram prog, string outDll,
                             IReadOnlyList<MachineInfo>? machines = null)
    {
        using (var pe = File.Create(outDll))
        {
            Compile(GenerateSource(prog), pe, OutputKind.ConsoleApplication, machines,
                    Path.GetFileNameWithoutExtension(outDll));
        }

        File.WriteAllText(Path.ChangeExtension(outDll, ".runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":" +
            "{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}," +
            "\"rollForward\":\"LatestMinor\"}}\n");

        string outDir = Path.GetDirectoryName(Path.GetFullPath(outDll))!;
        CopyBeside(typeof(Engine).Assembly.Location, outDir);
        if (machines != null)
            foreach (MachineInfo m in machines)
                CopyBeside(m.DllPath, outDir);
    }

    /// <summary>Weave a machine: machines/seq.shoddy -> Shoddy.Machines.Seq.dll
    /// beside it, exporting its defs and types with a manifest.
    ///
    /// With <paramref name="debug"/>, the instrumented machine weave
    /// (A6.2): same assembly name, same manifest, every line hooked —
    /// written to a bin/debug/ subfolder so a debug configuration can
    /// reference the instrumented artifact while the release one stays
    /// untouched, and the assembly's identity never forks.</summary>
    public static string WeaveMachine(ShoddyProgram prog, string sbPath,
                                      IReadOnlyList<MachineInfo>? machines = null,
                                      bool debug = false)
    {
        string outDll = debug ? MachineSet.DebugDllPathFor(sbPath) : MachineSet.DllPathFor(sbPath);
        string cls = MachineSet.ClassNameFor(sbPath);
        // Two different outer targets can both auto-build the same shared
        // machine (MachineResolve.Build); gate on the output so those
        // serialize too — the per-target gate in RunPure cannot see them.
        using IDisposable gate = MillGate.Hold(outDll);
        Directory.CreateDirectory(Path.GetDirectoryName(outDll)!);
        // Weave beside, then move into place: the gate serializes WRITERS,
        // but another project's compiler can be READING the previous weave
        // as a metadata reference at this very moment, and truncating the
        // file under it is a CS0009 in that build. The rename is atomic,
        // so a reader sees the old weave complete or the new one complete —
        // and the weave is deterministic, so rewriting is always safe.
        string tmp = outDll + ".weaving";
        using (FileStream pe = File.Create(tmp))
            Compile(GenerateSource(prog, cls, debug: debug, deps: machines, ownFile: sbPath), pe,
                    OutputKind.DynamicallyLinkedLibrary,
                    machines, $"Shoddy.Machines.{MachineSet.AssemblyStemFor(sbPath)}");
        File.Move(tmp, outDll, overwrite: true);
        return outDll;
    }

    static void Compile(string src, Stream pe, OutputKind kind,
                        IReadOnlyList<MachineInfo>? machines, string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(src);

        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        if (machines != null)
            foreach (MachineInfo m in machines)
                refs.Add(MetadataReference.CreateFromFile(m.DllPath));

        // Deterministic: identical inputs give byte-identical output. The
        // default is a fresh MVID per emit, under which the same weave run
        // twice differs — and C3.4 (either build of the mill produces the
        // same artifact for every shared verb) could never hold.
        var comp = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            refs,
            new CSharpCompilationOptions(kind, optimizationLevel: OptimizationLevel.Release,
                                         deterministic: true));

        EmitResult result = comp.Emit(pe);
        if (!result.Success)
        {
            string dump = Path.Combine(Path.GetTempPath(), assemblyName + ".woven.cs");
            File.WriteAllText(dump, src);
            string errs = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(10)
                .Select(d => d.ToString()));
            throw new ShoddyError(0,
                $"internal: generated C# failed to compile (source dumped to {dump}):\n{errs}");
        }
    }

    static void CopyBeside(string dll, string outDir)
    {
        string dest = Path.Combine(outDir, Path.GetFileName(dll));
        if (!string.Equals(Path.GetFullPath(dll), dest, StringComparison.Ordinal))
            File.Copy(dll, dest, true);
    }
}
