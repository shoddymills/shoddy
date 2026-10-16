// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text.RegularExpressions;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// A machine library generated whole: every source named becomes one
/// class under Shoddy.Machines, all meant for one assembly. The machines
/// are generated in dependency order, each seeded from the ones ahead of
/// it exactly as a program is seeded from a compiled machine, so the
/// code one machine weaves against another is the code it would weave
/// against the DLL — scoping, namespaces and effects included.
///
/// <para>Two consumers: <see cref="Weaver.WeaveLibrary"/> compiles the
/// units in process, and `mill gen --library` writes them as one C# file
/// for a project to compile with the ordinary C# compiler, which is how
/// the standard library is built — Debug and Release, PDBs, incremental
/// build and deterministic output all come from MSBuild for nothing.</para>
/// </summary>
public sealed class LibraryGen
{
    /// <summary>One generated class per source, in build order.</summary>
    public readonly List<(string Source, string Text)> Units = new();

    /// <summary>The library's own machines, as the resolver will read
    /// them back from the assembly.</summary>
    public readonly List<MachineInfo> Machines = new();

    readonly Dictionary<string, MachineInfo> external = new(StringComparer.Ordinal);

    /// <summary>Machines of other, compiled libraries that the sources
    /// reached: what the one compilation must reference.</summary>
    public IReadOnlyList<MachineInfo> External => external.Values.ToList();

    /// <summary>Generate the library. <paramref name="libraries"/> are
    /// compiled libraries the sources may include across (null means
    /// none); <paramref name="dbgSourceDir"/> is where the sources will
    /// sit relative to the DLL once installed, written into the
    /// instrumented weave's file table.</summary>
    public static LibraryGen Generate(IReadOnlyList<string> sources, bool debug,
                                      IEnumerable<string>? libraries, string dbgSourceDir)
    {
        var gen = new LibraryGen();
        List<string>? libs = libraries?.ToList();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in sources)
        {
            string full = Path.GetFullPath(s);
            string name = Path.GetFileName(full);
            if (byName.TryGetValue(name, out string? other) &&
                !string.Equals(other, full, StringComparison.OrdinalIgnoreCase))
                throw new ShoddyError(0, $"two sources named {name}: {other} and {full}. A library's machine names are unique");
            byName[name] = full;
        }

        foreach (string sb in Order(byName))
        {
            var set = new MachineSet(debug, libs);
            foreach (MachineInfo k in gen.Machines) set.Register(k);
            List<Line> lines = Lexer.ReadProgram(sb, (p, q) =>
            {
                if (set.TryResolve(p, q)) return true;
                throw new ShoddyError(0,
                    $"{Path.GetFileName(sb)} includes {Path.GetFileName(p)}, which is neither in this " +
                    $"library nor in a compiled one it was given (looked in: {set.Describe()})");
            });
            var seeded = new ShoddyProgram();
            set.SeedInto(seeded);
            ShoddyProgram parsed = Parser.Parse(lines, seeded);
            Lint.Result lint = Lint.Run(parsed, lines);
            foreach (string w in lint.Warnings) Console.Error.WriteLine(w);
            if (lint.UnknownWords.Count > 0)
            {
                foreach ((string msg, string? f, int ln) in lint.UnknownWords)
                    Console.Error.WriteLine($"{(f == null ? "?" : Path.GetFileName(f))}:{ln}: error: {msg}");
                (string m0, string? f0, int l0) = lint.UnknownWords[0];
                throw new ShoddyError(l0, f0, m0);
            }
            var cg = new CodeGen(parsed, MachineSet.ClassNameFor(sb), debug, set.Machines,
                                 ownFile: sb, dbgSourceDir: dbgSourceDir, standalone: false);
            string text = cg.Generate();
            gen.Units.Add((sb, text));
            gen.Machines.Add(cg.Manifest(assemblyName: "", dllPath: ""));
            foreach (MachineInfo m in set.Machines)
                if (m.DllPath.Length > 0) gen.external.TryAdd(m.ClassName, m);
        }
        return gen;
    }

    /// <summary>The library's own text as one file: the header once, then
    /// every unit.</summary>
    public string ToSource()
    {
        var sb = new System.Text.StringBuilder(CodeGen.Header);
        foreach ((string source, string text) in Units)
        {
            sb.Append("\n// ---- ").Append(Path.GetFileName(source)).Append(" ----\n\n");
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>The sources in dependency order: a machine after every
    /// machine of this library it includes. Includes that name no source
    /// here are another library's, or an error the resolution step
    /// reports with the right words. A cycle is refused by name.</summary>
    static List<string> Order(Dictionary<string, string> byName)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var indeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in byName.Keys) { edges[name] = new List<string>(); indeg[name] = 0; }
        foreach ((string name, string path) in byName)
            foreach (string inc in IncludesOf(path))
            {
                if (!byName.ContainsKey(inc) || string.Equals(inc, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                edges[inc].Add(name);
                indeg[name]++;
            }
        var ready = new SortedSet<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key),
                                          StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            string next = ready.Min!;
            ready.Remove(next);
            order.Add(byName[next]);
            foreach (string dep in edges[next])
                if (--indeg[dep] == 0) ready.Add(dep);
        }
        if (order.Count < byName.Count)
        {
            string stuck = string.Join(", ", indeg.Where(kv => kv.Value > 0).Select(kv => kv.Key)
                                                   .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            throw new ShoddyError(0, $"include cycle among machines: {stuck}");
        }
        return order;
    }

    static readonly Regex IncludeLine = new(@"^\s*Include\s+""([^""]+)""", RegexOptions.IgnoreCase);

    /// <summary>The file names a source includes, read textually: enough
    /// to order a build, with the Lexer doing the real resolution.</summary>
    static IEnumerable<string> IncludesOf(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            Match m = IncludeLine.Match(line);
            if (m.Success) yield return Path.GetFileName(m.Groups[1].Value);
        }
    }

    /// <summary>Every machine source in a folder, or the file itself.</summary>
    public static List<string> SourcesIn(IEnumerable<string> targets)
    {
        var found = new List<string>();
        foreach (string t in targets)
        {
            if (Directory.Exists(t))
                found.AddRange(Directory.GetFiles(t, "*.shoddy").OrderBy(p => p, StringComparer.Ordinal));
            else if (File.Exists(t))
                found.Add(t);
            else
                throw new ShoddyError(0, $"cannot open '{t}'");
        }
        return found;
    }
}
