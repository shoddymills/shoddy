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
/// (`mill weave`), with a runtimeconfig.json, Shoddy.Runtime.dll and the
/// compiled libraries it links alongside so `dotnet program.dll` just
/// works. Machines compile the same way, minus the entry point, plus
/// their manifest: one alone (`mill machine`), or a whole library in one
/// assembly (`mill library`, or the library project over `mill gen
/// --library`).
/// </summary>
public static class Weaver
{
    public static string GenerateSource(ShoddyProgram prog, string? machineClass = null,
                                        bool debug = false,
                                        IReadOnlyList<MachineInfo>? deps = null,
                                        string? ownFile = null,
                                        string? dbgSourceDir = null) =>
        new CodeGen(prog, machineClass, debug, deps, ownFile, dbgSourceDir).Generate();

    /// <summary>Compile a debug-instrumented (perch) build to memory and
    /// return its Run entry point, which takes the program's output,
    /// input and arguments exactly as `mill run` gives them. Install the
    /// sink on <see cref="Engine.PendingSink"/> before invoking.
    ///
    /// The machines are whatever the caller resolved. The debugger
    /// resolves them through a debug <see cref="MachineSet"/>, so they are
    /// the instrumented weaves and every machine line is steppable.</summary>
    public static Func<TextWriter, TextReader, string[], int> LoadDebug(
        ShoddyProgram prog, IReadOnlyList<MachineInfo>? machines = null) =>
        BuildDebug(prog, machines).Run;

    /// <summary>The debug build in full: the entry point, and the
    /// evaluator that compiles an expression against the running program
    /// (<see cref="DebugBuild.Evaluate"/>).</summary>
    public static DebugBuild BuildDebug(ShoddyProgram prog, IReadOnlyList<MachineInfo>? machines = null)
    {
        var gen = new CodeGen(prog, null, debug: true, deps: machines);
        string src = gen.Generate();
        byte[] image;
        using (var pe = new MemoryStream())
        {
            Compile(src, pe, OutputKind.ConsoleApplication, machines,
                    "perch-" + Guid.NewGuid().ToString("N"));
            image = pe.ToArray();
        }
        Assembly asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image));
        MethodInfo run = asm.GetType("Woven")!.GetMethod("Run")!;
        return new DebugBuild(prog, machines ?? Array.Empty<MachineInfo>(), image,
            gen.DefMembers, gen.TypeMembers,
            (o, i, args) => (int)run.Invoke(null, new object?[] { o, i, args })!);
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
        // Libraries were Assembly.LoadFrom'd while reading manifests, which
        // lands them in the default context — references resolve by name.
        Assembly asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(pe);
        MethodInfo run = asm.GetType("Woven")!.GetMethod("Run")!;
        return (int)run.Invoke(null, new object?[] { output, input, args ?? Array.Empty<string>() })!;
    }

    /// <summary>The distinct compiled assemblies behind a set of machines:
    /// one per library, one per woven core. What a weave references, and
    /// what travels beside its output.</summary>
    public static IEnumerable<string> LibrariesOf(IReadOnlyList<MachineInfo>? machines) =>
        machines == null
            ? Array.Empty<string>()
            : machines.Where(m => m.DllPath.Length > 0)
                      .Select(m => Path.GetFullPath(m.DllPath))
                      .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Weave a program to a runnable assembly at
    /// <paramref name="outDll"/>, with its runtime config, the runtime
    /// and the libraries it links beside it.
    ///
    /// Every file lands through <see cref="Publish"/>, never by truncating
    /// in place. Two projects in one solution weave the same mill under
    /// parallel MSBuild; the gate in RunPure serializes the two mill
    /// processes, but not the FIRST project's reference resolution, which
    /// is reading this DLL while the second project's weave rewrites it.
    /// Truncating under that reader is an MSB3246 "bad image" in its
    /// build.</summary>
    public static void Weave(ShoddyProgram prog, string outDll,
                             IReadOnlyList<MachineInfo>? machines = null)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outDll))!;
        Directory.CreateDirectory(outDir);
        using (var pe = new MemoryStream())
        {
            Compile(GenerateSource(prog), pe, OutputKind.ConsoleApplication, machines,
                    Path.GetFileNameWithoutExtension(outDll));
            Publish(outDll, pe.ToArray());
        }

        Publish(Path.ChangeExtension(outDll, ".runtimeconfig.json"), System.Text.Encoding.UTF8.GetBytes(
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":" +
            "{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}," +
            "\"rollForward\":\"LatestMinor\"}}\n"));

        CopyBeside(typeof(Engine).Assembly.Location, outDir);
        foreach (string dll in LibrariesOf(machines))
            CopyBeside(dll, outDir);
    }

    /// <summary>Weave one machine alone: halifax-core.shoddy ->
    /// Shoddy.Machines.Halifax-core.dll at <paramref name="outDll"/>,
    /// exporting its defs and types with a manifest on its class.
    ///
    /// With <paramref name="debug"/>, the instrumented machine weave
    /// (A6.2): same assembly name, same manifest, every line hooked —
    /// written wherever the caller says, so a debug configuration can
    /// reference the instrumented artifact while the release one stays
    /// untouched, and the assembly's identity never forks.</summary>
    public static string WeaveMachine(ShoddyProgram prog, string sbPath, string outDll,
                                      IReadOnlyList<MachineInfo>? machines = null,
                                      bool debug = false)
    {
        string cls = MachineSet.ClassNameFor(sbPath);
        // Two different outer targets can both weave the same shared
        // machine; gate on the output so those serialize too — the
        // per-target gate in RunPure cannot see them.
        using IDisposable gate = MillGate.Hold(outDll);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outDll))!);
        // Weave beside, then move into place: the gate serializes WRITERS,
        // but another project's compiler can be READING the previous weave
        // as a metadata reference at this very moment, and truncating the
        // file under it is a CS0009 in that build. The rename is atomic,
        // so a reader sees the old weave complete or the new one complete —
        // and the weave is deterministic, so rewriting is always safe.
        string tmp = outDll + ".weaving";
        using (FileStream pe = File.Create(tmp))
            Compile(GenerateSource(prog, cls, debug: debug, deps: machines, ownFile: sbPath,
                                   dbgSourceDir: RelativeDir(outDll, sbPath)),
                    pe, OutputKind.DynamicallyLinkedLibrary,
                    machines, MachineSet.AssemblyNameFor(sbPath));
        File.Move(tmp, outDll, overwrite: true);
        return outDll;
    }

    /// <summary>Weave a whole library — every machine source named, in
    /// dependency order — into one assembly at <paramref name="outDll"/>.
    /// <paramref name="libraries"/> are the compiled libraries the
    /// sources may include across (the seeds include the machines they
    /// bridge); <paramref name="dbgSourceDir"/> is where the sources will
    /// sit relative to the DLL once installed, for the instrumented
    /// weave's file table.</summary>
    public static string WeaveLibrary(IReadOnlyList<string> sources, string outDll, string assemblyName,
                                      bool debug = false, IEnumerable<string>? libraries = null,
                                      string? dbgSourceDir = null)
    {
        if (sources.Count == 0) throw new ShoddyError(0, "a library needs at least one machine source");
        LibraryGen lib = LibraryGen.Generate(sources, debug, libraries,
                                             dbgSourceDir ?? RelativeDir(outDll, sources[0]));
        using IDisposable gate = MillGate.Hold(outDll);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outDll))!);
        string tmp = outDll + ".weaving";
        using (FileStream pe = File.Create(tmp))
            Compile(lib.Units.Select(u => CodeGen.Header + u.Text).ToList(), pe,
                    OutputKind.DynamicallyLinkedLibrary, lib.External, assemblyName);
        File.Move(tmp, outDll, overwrite: true);
        return outDll;
    }

    /// <summary>Where a source's folder sits relative to the folder a DLL
    /// is written to, with '/' throughout so a weave made on Windows and
    /// one made elsewhere carry the same text.</summary>
    public static string RelativeDir(string outDll, string sbPath) =>
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(outDll))!,
                             Path.GetDirectoryName(Path.GetFullPath(sbPath))!)
            .Replace(Path.DirectorySeparatorChar, '/');

    internal static void Compile(string src, Stream pe, OutputKind kind,
                                 IReadOnlyList<MachineInfo>? machines, string assemblyName,
                                 IEnumerable<MetadataReference>? extra = null) =>
        Compile(new[] { src }, pe, kind, machines, assemblyName, extra);

    internal static void Compile(IReadOnlyList<string> sources, Stream pe, OutputKind kind,
                                 IReadOnlyList<MachineInfo>? machines, string assemblyName,
                                 IEnumerable<MetadataReference>? extra = null)
    {
        SyntaxTree[] trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();

        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        foreach (string dll in LibrariesOf(machines))
            refs.Add(MetadataReference.CreateFromFile(dll));
        if (extra != null) refs.AddRange(extra);

        // Deterministic: identical inputs give byte-identical output. The
        // default is a fresh MVID per emit, under which the same weave run
        // twice differs — and C3.4 (either build of the mill produces the
        // same artifact for every shared verb) could never hold.
        var comp = CSharpCompilation.Create(
            assemblyName,
            trees,
            refs,
            new CSharpCompilationOptions(kind, optimizationLevel: OptimizationLevel.Release,
                                         deterministic: true));

        EmitResult result = comp.Emit(pe);
        if (!result.Success)
        {
            string dump = Path.Combine(Path.GetTempPath(), assemblyName + ".woven.cs");
            File.WriteAllText(dump, string.Join("\n// ---- next unit ----\n", sources));
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
            Publish(dest, File.ReadAllBytes(dll));
    }

    /// <summary>Put <paramref name="bytes"/> at <paramref name="dest"/>
    /// without a reader ever seeing a partial file.
    ///
    /// Identical content is left alone and only its timestamp moves on:
    /// the weave is deterministic, so the second of two parallel projects
    /// weaving one mill produces exactly the first one's bytes, and not
    /// rewriting them is what keeps it off the file the first project is
    /// reading. The timestamp still moves so MSBuild's Inputs/Outputs check
    /// sees the output as current; setting it needs no data access, so an
    /// open reader does not block it.
    ///
    /// Different content is written beside and renamed over, as
    /// WeaveMachine does: a reader sees the old file whole or the new one
    /// whole.</summary>
    static void Publish(string dest, byte[] bytes)
    {
        if (File.Exists(dest) && new FileInfo(dest).Length == bytes.Length &&
            File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes))
        {
            File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
            return;
        }
        string tmp = dest + ".weaving";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, dest, overwrite: true);
    }
}
