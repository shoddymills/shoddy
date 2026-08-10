// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// `mill manifest` — verify (exit 0/1) or, with --write, generate and
/// refresh a mill's `mill.manifest`.
///
/// The machine-dependency section is generated, never hand-maintained:
/// generation walks the mill's Includes with the same resolver every
/// other verb uses, and the resolved MachineSet IS the transitive
/// closure — there is no second derivation to drift. Everything else in
/// the file is hand-authored fact (name, modes, capabilities, assets,
/// goldens) and is preserved by --write, validated by both paths.
///
/// Validation is structural checks in code over System.Text.Json's DOM
/// — BCL only, zero new package references. The JSON Schema published in
/// docs/schemas/ exists for editors, not for this verb.
///
/// A clean verify prints nothing and exits 0, like every other verb.
/// </summary>
public static class Manifest
{
    public const string FileName = "mill.manifest";

    public const string SchemaUrl =
        "https://shoddymills.github.io/shoddy/schemas/mill.manifest.json";

    /// <summary>The capability vocabulary — the mill's own seam list,
    /// never host concepts (A3.3). One list, three consumers: hosts'
    /// pre-configuration, the build gate, and the desktop mill's own
    /// gates where gates exist.</summary>
    public static readonly string[] Capabilities =
        { "net", "file", "terminal", "vt100", "keys", "scribbler", "buzzer", "clock", "random" };

    /// <summary>Entry point for the verb. <paramref name="target"/> is
    /// the mill folder or any .shoddy file in it.</summary>
    public static int Run(string target, bool write)
    {
        string folder = Directory.Exists(target)
            ? target
            : Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".";
        string path = Path.Combine(folder, FileName);

        if (!write && !File.Exists(path))
        {
            Console.Error.WriteLine(
                $"mill: no {FileName} in {folder} — generate one with: mill manifest --write {target}");
            return 1;
        }

        JsonObject root;
        if (File.Exists(path))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(path),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine($"mill: {path}: not valid JSON — {e.Message}");
                return 1;
            }
            if (parsed is not JsonObject o)
            {
                Console.Error.WriteLine($"mill: {path}: the manifest must be a JSON object");
                return 1;
            }
            root = o;
        }
        else
        {
            root = Skeleton(folder);
        }

        List<string> errors = Check(root, folder);
        if (errors.Count > 0)
        {
            foreach (string e in errors)
                Console.Error.WriteLine($"mill: {path}: {e}");
            return 1;
        }

        List<string> truth = Closure(root, folder);

        if (write)
        {
            root["machines"] = new JsonArray(truth.Select(m => (JsonNode)m).ToArray());
            File.WriteAllText(path, root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.Error.WriteLine(
                $"mill: wrote {path} ({truth.Count} machine{(truth.Count == 1 ? "" : "s")})");
            return 0;
        }

        var declared = root["machines"] is JsonArray arr
            ? arr.Select(n => n?.GetValue<string>() ?? "").OrderBy(s => s, StringComparer.Ordinal).ToList()
            : null;
        if (declared == null || !declared.SequenceEqual(truth))
        {
            Console.Error.WriteLine($"mill: {path}: the machines section is stale.");
            Console.Error.WriteLine(
                $"  manifest: {(declared == null ? "(missing)" : string.Join(", ", declared))}");
            Console.Error.WriteLine($"  truth:    {string.Join(", ", truth)}");
            Console.Error.WriteLine($"  regenerate with: mill manifest --write {target}");
            return 1;
        }
        return 0;
    }

    /// <summary>`mill manifest --inputs` — the build's per-mill plan:
    /// everything ShoddyWeave needs, computed here so the toolchain is
    /// the one source of truth for names and artifact paths. One line
    /// per fact, kind-prefixed, paths absolute:
    ///   name|…        the manifest's name
    ///   core|…        the core file (Mode N input), when declared
    ///   shell|…       the shell file (Mode T input), when declared
    ///   outn|…        the machine DLL the core weaves to
    ///   outndebug|…   its instrumented (--debug) twin
    ///   outt|…        the console DLL the shell weaves to
    ///   source|…      the mill's own files, spliced siblings included
    ///   machine|…     a transitive-closure machine DLL
    ///   machinesrc|…  that machine's source (its change re-weaves too)
    ///   asset|…       a declared asset file, globs and folders expanded
    ///   manifest|…    the manifest itself
    /// ShoddyWeave reads this into its items; nothing else parses it.</summary>
    public static int Inputs(string target)
    {
        (string folder, string path) = Locate(target);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"mill: no {FileName} in {folder}");
            return 1;
        }
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
        {
            Console.Error.WriteLine($"mill: {path}: the manifest must be a JSON object");
            return 1;
        }

        Console.WriteLine($"name|{root["name"]?.GetValue<string>() ?? Path.GetFileName(folder)}");

        var sources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var set = new MachineSet();
        foreach (string key in new[] { "shell", "core" })
        {
            if (root[key]?.GetValue<string>() is not string file) continue;
            string full = Path.GetFullPath(Path.Combine(folder, file));
            Console.WriteLine($"{key}|{full}");
            if (key == "core")
            {
                Console.WriteLine($"outn|{MachineSet.DllPathFor(full)}");
                Console.WriteLine($"outndebug|{MachineSet.DebugDllPathFor(full)}");
            }
            else
            {
                Console.WriteLine($"outt|{Path.ChangeExtension(full, ".dll")}");
            }
            foreach (Line l in Lexer.ReadProgram(full,
                                                 (p, q) => ResolveDependency(set, folder, p, q)))
                if (l.File != null) sources.Add(Path.GetFullPath(l.File));
        }
        foreach (string s in sources)
            Console.WriteLine($"source|{s}");
        foreach (MachineInfo m in set.Machines.OrderBy(m => m.DllPath, StringComparer.OrdinalIgnoreCase))
        {
            string dll = Path.GetFullPath(m.DllPath);
            Console.WriteLine($"machine|{dll}");
            // bin/Shoddy.Machines.X.dll -> ../x.shoddy, exact because the
            // assembly stem keeps the source's own spelling.
            string stem = m.AssemblyName.Replace("Shoddy.Machines.", "").ToLowerInvariant();
            string src = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(dll)!, "..", stem + ".shoddy"));
            if (File.Exists(src)) Console.WriteLine($"machinesrc|{src}");
        }
        foreach (string a in StrArray(root, "assets", new List<string>()))
        {
            string full = Path.GetFullPath(Path.Combine(folder, a));
            if (Directory.Exists(full))
                foreach (string f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    Console.WriteLine($"asset|{Path.GetFullPath(f)}");
            else if (File.Exists(full))
                Console.WriteLine($"asset|{full}");
            else
                foreach (string f in Directory.EnumerateFiles(
                             Path.GetDirectoryName(full) ?? folder, Path.GetFileName(full)))
                    Console.WriteLine($"asset|{Path.GetFullPath(f)}");
        }
        Console.WriteLine($"manifest|{Path.GetFullPath(path)}");
        return 0;
    }

    /// <summary>`mill manifest --gate GRANTS [--mode M]` — the capability
    /// gate ShoddyWeave applies per mill (C2.4). GRANTS is the item's
    /// semicolon list, possibly empty: default-deny means an ungranted
    /// REQUIRED capability refuses the build, naming the capability and
    /// printing the exact line to add — the refusal is a requirement,
    /// not a nicety (C2.4b). An ungranted OPTIONAL capability warns and
    /// proceeds with the seam's null behaviour. A grant outside the
    /// manifest's declarations warns too: it grants nothing.</summary>
    public static int Gate(string target, string grants, string? mode)
    {
        (string folder, string path) = Locate(target);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"mill: no {FileName} in {folder}");
            return 1;
        }
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
        {
            Console.Error.WriteLine($"mill: {path}: the manifest must be a JSON object");
            return 1;
        }
        List<string> errors = Check(root, folder);
        if (errors.Count > 0)
        {
            foreach (string e in errors)
                Console.Error.WriteLine($"mill: {path}: {e}");
            return 1;
        }
        string name = root["name"]!.GetValue<string>();

        if (mode != null)
        {
            var modes = root["modes"]!.AsArray().Select(n => n!.GetValue<string>());
            if (!modes.Contains(mode))
            {
                Console.Error.WriteLine(
                    $"error SHODDY1001: mill '{name}' does not support mode {mode} — " +
                    $"its manifest declares [{string.Join(", ", modes)}]. " +
                    $"Declared at {path}.");
                return 1;
            }
        }

        var granted = grants.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                        StringSplitOptions.TrimEntries)
                            .Select(g => g.ToLowerInvariant())
                            .ToHashSet(StringComparer.Ordinal);
        foreach (string g in granted)
            if (!Capabilities.Contains(g))
            {
                Console.Error.WriteLine(
                    $"error SHODDY1004: Grant=\"{g}\" on mill '{name}' is not a capability — " +
                    $"the vocabulary is: {string.Join(", ", Capabilities)}.");
                return 1;
            }

        bool refused = false;
        if (root["capabilities"] is JsonObject caps)
        {
            foreach (var kv in caps)
            {
                bool optional = kv.Value is JsonObject o &&
                    o["optional"] is JsonValue v && v.TryGetValue(out bool b) && b;
                if (granted.Contains(kv.Key)) continue;
                if (optional)
                {
                    Console.Error.WriteLine(
                        $"warning SHODDY1003: mill '{name}': optional capability " +
                        $"'{kv.Key}' is not granted; it runs with the seam's null behaviour.");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"error SHODDY1002: mill '{name}' requires capability '{kv.Key}', " +
                        $"which this project has not granted. Add Grant=\"{kv.Key}\" to its " +
                        $"<ShoddyMill> item, or remove the mill. Declared at {path}.");
                    refused = true;
                }
            }
            foreach (string g in granted)
                if (caps[g] is null)
                    Console.Error.WriteLine(
                        $"warning SHODDY1005: mill '{name}' does not declare capability " +
                        $"'{g}' — the grant grants nothing.");
        }
        else
        {
            foreach (string g in granted)
                Console.Error.WriteLine(
                    $"warning SHODDY1005: mill '{name}' does not declare capability " +
                    $"'{g}' — the grant grants nothing.");
        }
        return refused ? 1 : 0;
    }

    static (string Folder, string ManifestPath) Locate(string target)
    {
        string folder = Directory.Exists(target)
            ? target
            : Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".";
        return (folder, Path.Combine(folder, FileName));
    }

    /// <summary>The transitive machine closure of the mill's sources —
    /// shell and core, each walked with the resolver every other verb
    /// uses. Source names, lower-case, sorted, without .shoddy.</summary>
    static List<string> Closure(JsonObject root, string folder)
    {
        var set = new MachineSet();
        foreach (string key in new[] { "shell", "core" })
        {
            if (root[key]?.GetValue<string>() is not string file) continue;
            Lexer.ReadProgram(Path.Combine(folder, file),
                              (p, q) => ResolveDependency(set, folder, p, q));
        }
        return set.Machines
            .Select(m => m.AssemblyName.Replace("Shoddy.Machines.", "").ToLowerInvariant())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The derivation's resolver: like every other verb's, but
    /// the mill's OWN files always splice — a woven core sitting in the
    /// mill's bin/ must not turn the mill into a dependency of itself,
    /// and the machines list is the closure of what the mill REACHES,
    /// never what it IS.</summary>
    static bool ResolveDependency(MachineSet set, string folder, string sbPath, string? qual)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(sbPath)) ?? "";
        string own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(dir, own, StringComparison.OrdinalIgnoreCase)) return false;
        return MachineResolve.Resolve(set, sbPath, qual);
    }

    /// <summary>A fresh manifest for a mill that has none: the
    /// hand-authored fields seeded from what the folder shows —
    /// convention-named shell and core, test.shoddy as the golden —
    /// and an empty capability set for the author to complete.</summary>
    static JsonObject Skeleton(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        string shell = name + ".shoddy";
        string core = name + "-core.shoddy";
        var modes = new JsonArray();
        if (File.Exists(Path.Combine(folder, shell))) modes.Add("T");
        if (File.Exists(Path.Combine(folder, core))) modes.Add("N");

        var root = new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["name"] = name,
            ["version"] = "1.0",
        };
        if (File.Exists(Path.Combine(folder, core))) root["core"] = core;
        if (File.Exists(Path.Combine(folder, shell))) root["shell"] = shell;
        root["modes"] = modes;
        root["machines"] = new JsonArray();
        root["capabilities"] = new JsonObject();
        root["assets"] = new JsonArray();
        var goldens = new JsonArray();
        if (File.Exists(Path.Combine(folder, "test.shoddy"))) goldens.Add("test.shoddy");
        root["goldens"] = goldens;
        return root;
    }

    /// <summary>The structural checks — every rule the toolchain holds a
    /// manifest to. The published schema mirrors these for editors; a
    /// test validates the shipped manifests against this code so the two
    /// cannot drift silently.</summary>
    static List<string> Check(JsonObject root, string folder)
    {
        var errors = new List<string>();
        var known = new[] { "$schema", "name", "version", "core", "shell",
                            "modes", "machines", "capabilities", "assets", "goldens" };
        foreach (var kv in root)
            if (!known.Contains(kv.Key))
                errors.Add($"unknown field '{kv.Key}'");

        if (Str(root, "name", errors, required: true) is null) { }
        Str(root, "version", errors);
        Str(root, "$schema", errors);

        var modes = new List<string>();
        if (root["modes"] is JsonArray ma)
        {
            foreach (JsonNode? n in ma)
            {
                string? m = (n as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
                if (m is not ("T" or "N")) errors.Add("modes entries must be \"T\" or \"N\"");
                else if (modes.Contains(m)) errors.Add($"mode \"{m}\" is listed twice");
                else modes.Add(m);
            }
            if (modes.Count == 0) errors.Add("modes must name at least one of \"T\", \"N\"");
        }
        else
        {
            errors.Add("modes is required: an array naming \"T\", \"N\" or both");
        }

        string? core = Str(root, "core", errors);
        string? shell = Str(root, "shell", errors);
        if (modes.Contains("N") && core is null)
            errors.Add("mode \"N\" requires a core entry");
        if (modes.Contains("T") && shell is null)
            errors.Add("mode \"T\" requires a shell entry");
        foreach ((string key, string? file) in new[] { ("core", core), ("shell", shell) })
            if (file != null && !File.Exists(Path.Combine(folder, file)))
                errors.Add($"{key} '{file}' does not exist in {folder}");

        if (root["machines"] is JsonNode mn && mn is not JsonArray)
            errors.Add("machines must be an array of machine names");

        if (root["capabilities"] is JsonNode cn)
        {
            if (cn is not JsonObject caps)
            {
                errors.Add("capabilities must be an object");
            }
            else
            {
                foreach (var kv in caps)
                {
                    if (!Capabilities.Contains(kv.Key))
                    {
                        errors.Add($"unknown capability '{kv.Key}' — the vocabulary is: " +
                                   string.Join(", ", Capabilities));
                        continue;
                    }
                    switch (kv.Value)
                    {
                        case JsonValue v when v.TryGetValue(out bool b):
                            if (!b) errors.Add(
                                $"capability '{kv.Key}': false is not a declaration — omit it instead");
                            break;
                        case JsonObject detail:
                            CheckCapabilityDetail(kv.Key, detail, errors);
                            break;
                        default:
                            errors.Add($"capability '{kv.Key}' must be true or an object");
                            break;
                    }
                }
            }
        }

        StrArray(root, "assets", errors);
        foreach (string g in StrArray(root, "goldens", errors))
            if (!File.Exists(Path.Combine(folder, g)))
                errors.Add($"golden '{g}' does not exist in {folder}");

        return errors;
    }

    static void CheckCapabilityDetail(string cap, JsonObject detail, List<string> errors)
    {
        var allowed = new[] { "optional", "read", "write", "width", "height" };
        foreach (var kv in detail)
        {
            if (!allowed.Contains(kv.Key))
            {
                errors.Add($"capability '{cap}': unknown key '{kv.Key}'");
                continue;
            }
            bool ok = kv.Key switch
            {
                "optional" => kv.Value is JsonValue v && v.TryGetValue(out bool _),
                "read" or "write" => kv.Value is JsonArray a &&
                    a.All(n => n is JsonValue s && s.TryGetValue(out string? _)),
                _ => kv.Value is JsonValue nv && nv.TryGetValue(out double _),
            };
            if (!ok) errors.Add($"capability '{cap}': '{kv.Key}' has the wrong shape");
        }
        if ((detail.ContainsKey("read") || detail.ContainsKey("write")) && cap != "file")
            errors.Add($"capability '{cap}': read/write paths belong to 'file'");
        if ((detail.ContainsKey("width") || detail.ContainsKey("height")) && cap != "scribbler")
            errors.Add($"capability '{cap}': dimensions belong to 'scribbler'");
    }

    static string? Str(JsonObject root, string key, List<string> errors, bool required = false)
    {
        JsonNode? n = root[key];
        if (n is null)
        {
            if (required) errors.Add($"{key} is required");
            return null;
        }
        if (n is JsonValue v && v.TryGetValue(out string? s)) return s;
        errors.Add($"{key} must be a string");
        return null;
    }

    static List<string> StrArray(JsonObject root, string key, List<string> errors)
    {
        var result = new List<string>();
        JsonNode? n = root[key];
        if (n is null) return result;
        if (n is not JsonArray a)
        {
            errors.Add($"{key} must be an array of strings");
            return result;
        }
        foreach (JsonNode? item in a)
        {
            if (item is JsonValue v && v.TryGetValue(out string? s)) result.Add(s);
            else errors.Add($"{key} entries must be strings");
        }
        return result;
    }
}
