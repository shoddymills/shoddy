// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>The weave-time surface of one compiled machine, read from
/// the manifest attributes on its class.</summary>
public sealed class MachineInfo
{
    public required string AssemblyName;
    /// <summary>The compiled assembly the machine lives in. Empty while a
    /// library is being generated: the machine exists as C# source only,
    /// and the one compilation that follows holds every class.</summary>
    public required string DllPath;
    public required string ClassName;   // e.g. "Shoddy.Machines.Seq"
    /// <summary>The source file name the machine was woven from, as an
    /// Include spells it: seq.shoddy.</summary>
    public required string Source;
    /// <summary>The machines this one's Include chain reaches, by class
    /// name. Declared by the weave, so a pure re-export survives.</summary>
    public readonly List<string> DependsOn = new();
    public readonly Dictionary<string, string> Defs = new();   // name -> method
    public readonly Dictionary<string, (int Pops, int Pushes)> DefEffects = new();
    public readonly Dictionary<string, (string Field, TypeDef Shape)> Types = new();

    public static MachineInfo From(Type klass, string dllPath)
    {
        var m = klass.GetCustomAttribute<ShoddyMachineAttribute>()
            ?? throw new ShoddyError(0, $"'{klass.FullName}' in '{dllPath}' is not a Shoddy machine (no manifest)");
        var info = new MachineInfo
        {
            AssemblyName = klass.Assembly.GetName().Name!,
            DllPath = dllPath,
            ClassName = klass.FullName!,
            Source = m.Source,
        };
        foreach (var dep in klass.GetCustomAttributes<ShoddyDependsOnAttribute>())
            info.DependsOn.Add(dep.ClassName);
        foreach (var d in klass.GetCustomAttributes<ShoddyDefAttribute>())
        {
            info.Defs[d.Name] = d.Method;
            if (d.Pops >= 0 && d.Pushes >= 0)
                info.DefEffects[d.Name] = (d.Pops, d.Pushes);
        }
        foreach (var t in klass.GetCustomAttributes<ShoddyTypeAttribute>())
        {
            var shape = new TypeDef { Name = t.Name, Disp = t.Disp };
            for (int k = 0; k < t.Fields.Length; k++)
            {
                shape.Fields.Add(t.Fields[k]);
                shape.FDisp.Add(t.FDisp[k]);
            }
            info.Types[t.Name] = (t.Field, shape);
        }
        return info;
    }

    /// <summary>Every machine an assembly carries: one for a woven core,
    /// one per source for a library.</summary>
    public static List<MachineInfo> AllIn(Assembly asm, string dllPath)
    {
        var found = new List<MachineInfo>();
        foreach (Type t in MachineClasses(asm))
            found.Add(From(t, dllPath));
        return found;
    }

    /// <summary>The classes of an assembly that carry a machine manifest.</summary>
    public static IEnumerable<Type> MachineClasses(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetExportedTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        foreach (Type t in types)
            if (t.IsDefined(typeof(ShoddyMachineAttribute), inherit: false))
                yield return t;
    }
}

/// <summary>
/// Resolves Include paths to precompiled machines. A machine for
/// lib/seq.shoddy is the class Shoddy.Machines.Seq, in whichever compiled
/// library the set searches: the libraries named outright (ShoddyWeave
/// passes a consumer's references with --lib), else $SHODDYLIBBIN, else
/// the lib/ folder beside the running toolchain. Loading one pulls in the
/// manifests of the machines it reaches (compiled include-once — a
/// machine is loaded at most once, however many routes reach it).
///
/// <para>The set never builds anything and never judges staleness. A
/// library is built by its project, under MSBuild's own up-to-date check,
/// and a machine that is in no library is an error that names the
/// build to run.</para>
/// </summary>
public sealed class MachineSet
{
    public readonly List<MachineInfo> Machines = new();
    readonly Dictionary<string, MachineInfo> loaded = new(StringComparer.Ordinal);

    /// <summary>Machines registered as generated source, before any
    /// assembly holds them: a library being built seeds each machine
    /// from the ones generated ahead of it.</summary>
    readonly Dictionary<string, MachineInfo> known = new(StringComparer.Ordinal);

    readonly List<Library> libraries = new();

    /// <summary>
    /// A debug set resolves every machine to its instrumented weave —
    /// the debug/ subfolder of every library location, where the twins
    /// live under the same assembly names — dependencies included, so a
    /// debugged program can step into the machines it calls. One process
    /// loads one of the two: the debugger's set is a debug set from the
    /// start.
    /// </summary>
    public bool Debug { get; }

    /// <summary>A set over the libraries named, and nothing else: an
    /// Include resolves only against what the caller put in. The verbs
    /// name the toolchain's own library (<see cref="DefaultLibraries"/>)
    /// unless --lib says otherwise; a test names the library it built.
    /// Nothing is searched that nobody named, so a process never loads a
    /// library by accident, which matters because one process loads each
    /// library once.</summary>
    public MachineSet(bool debug = false, IEnumerable<string>? libraries = null)
    {
        Debug = debug;
        if (libraries != null)
            foreach (string dll in libraries)
                AddLibrary(dll);
    }

    /// <summary>A set over the toolchain's own library: what every verb
    /// uses when --lib names nothing.</summary>
    public static MachineSet Default(bool debug = false) =>
        new(debug, DefaultLibraries(debug));

    /// <summary>The compiled library folders a set searches when none
    /// are named, in order. A debug set reads each one's debug/ twin.</summary>
    public static IEnumerable<string> LibraryDirs(bool debug)
    {
        string? env = Environment.GetEnvironmentVariable("SHODDYLIBBIN");
        if (!string.IsNullOrEmpty(env))
            yield return debug ? Path.Combine(env, "debug") : env;
        string lib = Path.Combine(AppContext.BaseDirectory, "lib");
        yield return debug ? Path.Combine(lib, "debug") : lib;
    }

    /// <summary>Every machine library in the default library folders:
    /// the assemblies named Shoddy.Machines or Shoddy.Machines.X. A
    /// folder holds other assemblies too (a library's output carries the
    /// runtime it references), and loading one of those by path when the
    /// process already has it by name is a failure, so only the names
    /// the convention gives a library are taken.</summary>
    public static IEnumerable<string> DefaultLibraries(bool debug)
    {
        foreach (string dir in LibraryDirs(debug))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string dll in Directory.GetFiles(dir, "Shoddy.Machines*.dll").OrderBy(p => p, StringComparer.Ordinal))
                yield return dll;
        }
    }

    /// <summary>The compiled libraries this set searches, in order.</summary>
    public IEnumerable<string> LibraryPaths => libraries.Select(l => l.Path);

    /// <summary>Add a compiled library to the search. A second file
    /// carrying an assembly name already on the list is skipped: one
    /// simple name loads once per process, and the first named wins.</summary>
    public void AddLibrary(string dllPath)
    {
        string full = Path.GetFullPath(dllPath);
        if (!File.Exists(full)) return;
        string name;
        try { name = AssemblyName.GetAssemblyName(full).Name ?? ""; }
        catch (BadImageFormatException) { return; }       // not a .NET assembly
        if (libraries.Any(l => string.Equals(l.AssemblyName, name, StringComparison.OrdinalIgnoreCase)))
            return;
        libraries.Add(new Library(full, name));
    }

    /// <summary>Register a machine that exists as generated source only.</summary>
    public void Register(MachineInfo info) => known[info.ClassName] = info;

    sealed class Library
    {
        public readonly string Path;
        public readonly string AssemblyName;
        Dictionary<string, MachineInfo>? machines;

        public Library(string path, string assemblyName) { Path = path; AssemblyName = assemblyName; }

        /// <summary>The machines the assembly carries, loaded on first
        /// use. The load is by path into the default context, so the
        /// woven program's reference resolves by name afterwards.</summary>
        public Dictionary<string, MachineInfo> Machines
        {
            get
            {
                if (machines == null)
                {
                    Assembly asm;
                    try { asm = Assembly.LoadFrom(Path); }
                    catch (FileLoadException e)
                    {
                        throw new ShoddyError(0,
                            $"cannot load the machine library {Path}: {e.Message}. " +
                            "One process loads each library once, so a release weave and " +
                            "an instrumented one of the same library cannot both be used in it");
                    }
                    machines = MachineInfo.AllIn(asm, Path)
                        .ToDictionary(m => m.ClassName, m => m, StringComparer.Ordinal);
                }
                return machines;
            }
        }
    }

    /// <summary>The machines an Include named outright, as opposed to those
    /// dragged in behind them. Every loaded machine is linked; only these
    /// are <em>named</em>, because a machine's includes are its own
    /// business and not part of its surface.
    ///
    /// Include stats and you used to get seq and dict too, bare, in the
    /// same flat table — which made a machine's surface impossible to
    /// reason about and turned adding a word to seq into a possible
    /// collision anywhere in the tree.</summary>
    readonly HashSet<string> direct = new(StringComparer.Ordinal);

    /// <summary>Dependency class name -> the source of the machine that
    /// referenced it, so a refusal can say "seq.shoddy, which stats.shoddy
    /// includes" rather than leaving the reader to work out where it came
    /// from.</summary>
    readonly Dictionary<string, string> via = new(StringComparer.Ordinal);

    /// <summary>Class name -> the namespace its INCLUDE carried. A machine
    /// is never read as source, so its surface cannot be qualified the way
    /// a spliced file's is; the Lexer reports the AS here instead.
    ///
    /// This is what makes two machines that export the same word usable in
    /// one program: unqualified, a shared export name (net and turtle both
    /// export CLOSE) makes including both a hard error.</summary>
    readonly Dictionary<string, string> qualByClass = new(StringComparer.Ordinal);

    /// <summary>The woven class name: a valid C# identifier. A hyphen —
    /// every mill's <c>name-core.shoddy</c> — is legal in a file and an
    /// assembly name but not in a class, so non-alphanumerics become word
    /// breaks and the parts join PascalCase: halifax-core → HalifaxCore.
    /// For the plain stems machines have always used this is exactly the
    /// old capitalize-first rule. Nothing spells this name by convention:
    /// consumers read it from the ShoddyMachine attribute, and a core's
    /// assembly keeps the source's own stem (<see
    /// cref="AssemblyStemFor"/>) so the file visibly matches.</summary>
    public static string ClassNameFor(string sbPath)
    {
        string stem = Path.GetFileNameWithoutExtension(sbPath);
        var sb = new System.Text.StringBuilder(stem.Length);
        bool boundary = true;
        foreach (char c in stem)
        {
            if (!char.IsLetterOrDigit(c)) { boundary = true; continue; }
            sb.Append(boundary ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            boundary = false;
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>The class a machine source weaves to, in full:
    /// seq.shoddy -> Shoddy.Machines.Seq.</summary>
    public static string ClassFullNameFor(string sbPath) => "Shoddy.Machines." + ClassNameFor(sbPath);

    /// <summary>The stem a woven core's assembly carries: the source
    /// file's own, capitalized — Shoddy.Machines.Halifax-core.dll visibly
    /// matches halifax-core.shoddy.</summary>
    public static string AssemblyStemFor(string sbPath)
    {
        string stem = Path.GetFileNameWithoutExtension(sbPath);
        return char.ToUpperInvariant(stem[0]) + stem[1..].ToLowerInvariant();
    }

    /// <summary>The assembly name `mill machine` gives one machine woven
    /// alone: Shoddy.Machines.Halifax-core.</summary>
    public static string AssemblyNameFor(string sbPath) => $"Shoddy.Machines.{AssemblyStemFor(sbPath)}";

    /// <summary>The compiled machine for a source, or null when no
    /// library this set searches holds it.</summary>
    public MachineInfo? Find(string sbPath) => FindClass(ClassFullNameFor(sbPath));

    MachineInfo? FindClass(string className)
    {
        if (known.TryGetValue(className, out MachineInfo? k)) return k;
        foreach (Library lib in libraries)
            if (lib.Machines.TryGetValue(className, out MachineInfo? m)) return m;
        return null;
    }

    /// <summary>Where this set looked, for a message.</summary>
    public string Describe() =>
        libraries.Count == 0 ? "no compiled library" : string.Join(", ", LibraryPaths);

    /// <summary>The Lexer's externalInclude hook: true = a compiled
    /// machine satisfies this include, don't splice the source.
    /// <paramref name="qual"/> is the namespace the include carried, or
    /// null for a bare one; it is recorded here because the machine's
    /// source is never read and this is the only place the AS survives.</summary>
    public bool TryResolve(string sbPath, string? qual)
    {
        MachineInfo? m = Find(sbPath);
        if (m == null) return false;
        LoadRecursive(m);
        direct.Add(m.ClassName);
        if (qual != null) qualByClass[m.ClassName] = qual;
        return true;
    }

    void LoadRecursive(MachineInfo m)
    {
        if (!loaded.TryAdd(m.ClassName, m)) return;
        Machines.Add(m);
        foreach (string dep in m.DependsOn)
        {
            if (loaded.ContainsKey(dep)) continue;
            MachineInfo d = FindClass(dep)
                ?? throw new ShoddyError(0,
                    $"machine {m.Source} depends on {dep}, which is in no compiled library " +
                    $"(looked in: {Describe()})");
            via.TryAdd(dep, m.Source);
            LoadRecursive(d);
        }
    }

    /// <summary>Where a machine's source is now: the first machine library
    /// folder (<see cref="Lexer.LibDirs"/>) holding a file of its name,
    /// else beside its assembly's folder or up to two levels above it.
    /// Null when nowhere — the capability lint then reads no tag.</summary>
    public static string? SourcePathOf(MachineInfo m)
    {
        if (m.Source.Length == 0) return null;
        foreach (string dir in Lexer.LibDirs())
        {
            string p = Path.Combine(dir, m.Source);
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        if (m.DllPath.Length > 0)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(m.DllPath)) ?? "";
            foreach (string up in new[] { dir, Path.Combine(dir, ".."), Path.Combine(dir, "..", "..") })
            {
                string p = Path.Combine(up, m.Source);
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
        }
        return null;
    }

    /// <summary>Seed a program with the surface of every machine it
    /// included <em>by name</em>, so the parser and code generator resolve
    /// against precompiled code.
    ///
    /// Machines pulled in behind those are loaded and linked but not
    /// seeded: a machine exports what it declares, and what it includes is
    /// its own business. Their names go to <see cref="ShoddyProgram.Unseeded"/>
    /// instead, which is what lets the refusal name the include to add.</summary>
    public void SeedInto(ShoddyProgram prog)
    {
        foreach (MachineInfo m in Machines)
        {
            if (!direct.Contains(m.ClassName))
            {
                (string, string?) advice = (m.Source, via.GetValueOrDefault(m.ClassName));
                foreach (string bare in m.Defs.Keys) prog.Unseeded.TryAdd(bare, advice);
                foreach ((string bare, (string _, TypeDef shape)) in m.Types)
                {
                    prog.Unseeded.TryAdd(bare, advice);
                    foreach (string f in shape.Fields)
                        prog.Unseeded.TryAdd(f.ToUpperInvariant(), advice);
                }
                continue;
            }
            string? q = qualByClass.GetValueOrDefault(m.ClassName);
            foreach ((string bare, string method) in m.Defs)
            {
                string name = ShoddyProgram.Qualify(q, bare);
                if (prog.ExternalDefs.ContainsKey(name))
                    throw new ShoddyError(0, $"duplicate definition of {name} across machines" +
                        (q == null ? ". Include one of them under a namespace (AS)" : ""));
                prog.ExternalDefs[name] = $"{m.ClassName}.{method}";
                if (m.DefEffects.TryGetValue(bare, out (int, int) fx))
                    prog.ExternalEffects[name] = fx;
                prog.NoteBare(bare, name);
            }
            foreach ((string bare, (string field, TypeDef shape)) in m.Types)
            {
                string name = ShoddyProgram.Qualify(q, bare);
                if (prog.ExternalTypes.ContainsKey(name))
                    throw new ShoddyError(0, $"duplicate definition of {name} across machines" +
                        (q == null ? ". Include one of them under a namespace (AS)" : ""));
                prog.ExternalTypes[name] = new ExternalType(shape, $"{m.ClassName}.{field}");
                prog.NoteBare(bare, name);
                foreach (string f in shape.Fields) prog.NoteAccessor(q, f);
            }
        }
    }
}
