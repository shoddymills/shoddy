// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>The weave-time surface of one compiled machine DLL, read
/// from its assembly-attribute manifest.</summary>
public sealed class MachineInfo
{
    public required string AssemblyName;
    public required string DllPath;
    public required string ClassName;   // e.g. "Shoddy.Machines.Seq"
    public readonly Dictionary<string, string> Defs = new();   // name -> method
    public readonly Dictionary<string, (int Pops, int Pushes)> DefEffects = new();
    public readonly Dictionary<string, (string Field, TypeDef Shape)> Types = new();

    public static MachineInfo From(Assembly asm, string dllPath)
    {
        var m = asm.GetCustomAttribute<ShoddyMachineAttribute>()
            ?? throw new ShoddyError(0, $"'{dllPath}' is not a Shoddy machine (no manifest)");
        var info = new MachineInfo
        {
            AssemblyName = asm.GetName().Name!,
            DllPath = dllPath,
            ClassName = m.ClassName,
        };
        foreach (var d in asm.GetCustomAttributes<ShoddyDefAttribute>())
        {
            info.Defs[d.Name] = d.Method;
            if (d.Pops >= 0 && d.Pushes >= 0)
                info.DefEffects[d.Name] = (d.Pops, d.Pushes);
        }
        foreach (var t in asm.GetCustomAttributes<ShoddyTypeAttribute>())
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
}

/// <summary>
/// Resolves Include paths to precompiled machine DLLs. A machine for
/// lib/seq.shoddy is Shoddy.Machines.Seq.dll in a "bin" subdirectory beside
/// it (lib/bin/, mirroring how each mill drops its woven output in bin/ and
/// keeping the source tree clear of build artifacts); loading one pulls in
/// the manifests of the machines it references (compiled include-once —
/// an assembly is loaded at most once, however many routes reach it).
/// </summary>
public sealed class MachineSet
{
    /// <summary>Subdirectory beside a machine's source that holds its
    /// compiled DLL — build output, gitignored via the global bin/ rule.</summary>
    public const string BinDir = "bin";

    public readonly List<MachineInfo> Machines = new();
    readonly HashSet<string> loaded = new();

    /// <summary>The DLLs an Include named outright, as opposed to those
    /// dragged in behind them. Every loaded machine is linked; only these
    /// are <em>named</em>, because a machine's includes are its own
    /// business and not part of its surface.
    ///
    /// Include stats and you used to get seq and dict too, bare, in the
    /// same flat table — which made a machine's surface impossible to
    /// reason about and turned adding a word to seq into a possible
    /// collision anywhere in the tree.</summary>
    readonly HashSet<string> direct = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dependency assembly name -> the machine that referenced
    /// it, so a refusal can say "seq.shoddy, which stats.shoddy includes"
    /// rather than leaving the reader to work out where it came from.</summary>
    readonly Dictionary<string, string> via = new();

    /// <summary>DLL path -> the namespace its INCLUDE carried. A machine is
    /// never read as source, so its surface cannot be qualified the way a
    /// spliced file's is; the Lexer reports the AS here instead.
    ///
    /// This is what makes two machines that export the same word usable in
    /// one program: unqualified, a shared export name (net and turtle both
    /// export CLOSE) makes including both a hard error.</summary>
    readonly Dictionary<string, string> qualByDll = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This machine's include said AS <paramref name="qual"/>.
    /// Only the directly-included machine is qualified — the machines it
    /// pulls in as dependencies keep their own names, since the including
    /// program never named them.
    ///
    /// Called from <see cref="TryResolve"/> rather than by the caller, so
    /// that resolving a machine and recording its namespace cannot come
    /// apart. They were two separate hooks, and a caller that wired only
    /// the first got the AS silently discarded.</summary>
    void SetQualifier(string sbPath, string qual) =>
        qualByDll[DllPathFor(sbPath)] = qual;

    /// <summary>The woven class name: a valid C# identifier. A hyphen —
    /// every mill's <c>name-core.shoddy</c> — is legal in a file and an
    /// assembly name but not in a class, so non-alphanumerics become word
    /// breaks and the parts join PascalCase: halifax-core → HalifaxCore.
    /// For the plain stems machines have always used this is exactly the
    /// old capitalize-first rule. Nothing spells this name by convention:
    /// consumers read it from the ShoddyMachine attribute, and the
    /// assembly and DLL keep the source's own stem (<see
    /// cref="AssemblyStemFor"/>) so the assembly→source round trip in
    /// dependency advice stays exact.</summary>
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

    /// <summary>The stem the assembly and DLL carry: the source file's
    /// own, capitalized — Shoddy.Machines.Halifax-core.dll visibly matches
    /// halifax-core.shoddy, and <see cref="SourceNameFor(string)"/> can
    /// lower-case an assembly name straight back to the Include that
    /// names it.</summary>
    public static string AssemblyStemFor(string sbPath)
    {
        string stem = Path.GetFileNameWithoutExtension(sbPath);
        return char.ToUpperInvariant(stem[0]) + stem[1..].ToLowerInvariant();
    }

    /// <summary>The .shoddy name a machine assembly came from — what a
    /// user would write in an Include. Shoddy.Machines.Seq -> seq.shoddy.</summary>
    static string SourceNameFor(MachineInfo m) => SourceNameFor(m.AssemblyName);

    static string SourceNameFor(string assemblyName) =>
        assemblyName.Replace("Shoddy.Machines.", "").ToLowerInvariant() + ".shoddy";

    public static string DllPathFor(string sbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sbPath))!,
                     BinDir,
                     $"Shoddy.Machines.{AssemblyStemFor(sbPath)}.dll");

    /// <summary>The instrumented (debug) weave of the same machine:
    /// bin/debug/, same file name — the assembly's identity never forks,
    /// only which artifact a configuration references.</summary>
    public static string DebugDllPathFor(string sbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sbPath))!,
                     BinDir, "debug",
                     $"Shoddy.Machines.{AssemblyStemFor(sbPath)}.dll");

    /// <summary>The Lexer's externalInclude hook: true = a machine DLL
    /// satisfies this include, don't splice the source.
    /// <paramref name="qual"/> is the namespace the include carried, or
    /// null for a bare one; it is recorded here because the machine's
    /// source is never read and this is the only place the AS survives.</summary>
    public bool TryResolve(string sbPath, string? qual)
    {
        string dll = DllPathFor(sbPath);
        if (!File.Exists(dll)) return false;
        LoadRecursive(dll);
        direct.Add(dll);
        if (qual != null) SetQualifier(sbPath, qual);
        return true;
    }

    void LoadRecursive(string dllPath)
    {
        Assembly asm = Assembly.LoadFrom(dllPath);
        if (!loaded.Add(asm.GetName().Name!)) return;
        Machines.Add(MachineInfo.From(asm, dllPath));
        // Manifest-declared dependencies first: a pure re-export Include
        // (isam's seq) has no code reference for Roslyn to keep, so the
        // assembly-reference walk below cannot see it.
        foreach (var dep in asm.GetCustomAttributes<ShoddyDependsOnAttribute>())
        {
            if (loaded.Contains(dep.AssemblyName)) continue;
            string? dpath = FindDependencyDll(dllPath, dep.AssemblyName);
            if (dpath == null)
                throw new ShoddyError(0,
                    $"machine {asm.GetName().Name} declares dependency " +
                    $"{dep.AssemblyName}, but its DLL could not be found beside " +
                    $"{dllPath} or its machine library");
            via.TryAdd(dep.AssemblyName, SourceNameFor(asm.GetName().Name!));
            LoadRecursive(dpath);
        }
        foreach (AssemblyName rn in asm.GetReferencedAssemblies())
        {
            if (!rn.Name!.StartsWith("Shoddy.Machines.", StringComparison.Ordinal))
                continue;
            if (loaded.Contains(rn.Name)) continue;
            string? dep = FindDependencyDll(dllPath, rn.Name!);
            if (dep == null)
                throw new ShoddyError(0,
                    $"machine {asm.GetName().Name} references {rn.Name}, but its DLL " +
                    $"could not be found beside {dllPath} or its machine library");
            via.TryAdd(rn.Name, SourceNameFor(asm.GetName().Name!));
            LoadRecursive(dep);
        }
    }

    /// <summary>Where a dependency's DLL is: the machine library first —
    /// the canonical copy, and one identity per process, since loading
    /// the same simple name from two paths is a hard failure — then
    /// beside the needing machine (a scratch workspace whose machines
    /// are their own library), then one directory up (a reckoner seed
    /// in machines/seeds/bin depending on the machine it bridges). The
    /// library-first order is what keeps a mill's own bin/ — which
    /// carries COPIES of dependency DLLs beside its woven shell — from
    /// supplying stale identities. Null when no route has it.</summary>
    static string? FindDependencyDll(string dllPath, string assemblyName)
    {
        foreach (string lib in Shoddy.Devil.Lexer.LibDirs())
        {
            string inLib = Path.Combine(lib, BinDir, assemblyName + ".dll");
            if (File.Exists(inLib)) return Path.GetFullPath(inLib);
        }
        string dir = Path.GetDirectoryName(dllPath)!;
        string sibling = Path.Combine(dir, assemblyName + ".dll");
        if (File.Exists(sibling)) return sibling;
        string upOneLevel = Path.Combine(dir, "..", "..", "bin", assemblyName + ".dll");
        return File.Exists(upOneLevel) ? upOneLevel : null;
    }

    /// <summary>Seed a program with the surface of every machine it
    /// included <em>by name</em>, so the parser and code generator resolve
    /// against precompiled code.
    ///
    /// Machines pulled in behind those are loaded and linked but not
    /// seeded: a machine exports what it declares, and what it includes is
    /// its own business. Their names go to <see cref="Unseeded"/> instead,
    /// which is what lets the refusal name the include to add.</summary>
    public void SeedInto(ShoddyProgram prog)
    {
        foreach (MachineInfo m in Machines)
        {
            if (!direct.Contains(m.DllPath))
            {
                (string, string?) advice = (SourceNameFor(m), via.GetValueOrDefault(m.AssemblyName));
                foreach (string bare in m.Defs.Keys) prog.Unseeded.TryAdd(bare, advice);
                foreach ((string bare, (string _, TypeDef shape)) in m.Types)
                {
                    prog.Unseeded.TryAdd(bare, advice);
                    foreach (string f in shape.Fields)
                        prog.Unseeded.TryAdd(f.ToUpperInvariant(), advice);
                }
                continue;
            }
            string? q = qualByDll.GetValueOrDefault(m.DllPath);
            foreach ((string bare, string method) in m.Defs)
            {
                string name = ShoddyProgram.Qualify(q, bare);
                if (prog.ExternalDefs.ContainsKey(name))
                    throw new ShoddyError(0, $"duplicate definition of {name} across machines" +
                        (q == null ? " — include one of them under a namespace (AS)" : ""));
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
                        (q == null ? " — include one of them under a namespace (AS)" : ""));
                prog.ExternalTypes[name] = new ExternalType(shape, $"{m.ClassName}.{field}");
                prog.NoteBare(bare, name);
                foreach (string f in shape.Fields) prog.NoteAccessor(q, f);
            }
        }
    }
}
