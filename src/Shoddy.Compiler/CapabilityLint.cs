// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text.Json;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// The capability lint (A5.5): for a mill that carries a manifest, an
/// undeclared-but-used capability and a declared-but-unused capability
/// each produce a diagnostic. Warn, never fail — same doctrine, same
/// stderr channel and same layer as the stack-effect linter, so the
/// existing extension surfaces it with no new code.
///
/// The word→capability mapping is derived, never hand-maintained, from
/// three mechanical sources:
///   1. machines carry a capability tag — a `Rem Capability: X` header
///      line, read textually the way docs ground truth is read;
///   2. a builtin→capability table below, for capabilities that are
///      engine builtins rather than machines;
///   3. the body of every machine word the program calls, parsed from
///      the machine's source: what that body calls counts, through the
///      table, through further machine words, and through the tags.
/// The third exists because a tag is all-or-nothing per machine. math
/// carries none, being pure almost throughout, yet its Pick, RandInt,
/// RandRange and Chance draw on RND; read by tag alone, devils-dust and
/// invaders used random numbers and were told they used none.
///
/// This derivation is the lint's alone. `mill manifest` checks the
/// machines section, and the ShoddyWeave gate grants from the manifest's
/// declarations; neither reads what a mill reaches.
///
/// A program with no mill.manifest beside it has declared nothing and
/// gets no capability diagnostics — otherwise every plain program would
/// warn about undeclared `terminal` on its first Print.
/// </summary>
public static class CapabilityLint
{
    /// <summary>Capabilities that are engine builtins, not machine words.
    /// NETALLOWED is deliberately absent: asking whether the network is
    /// armed is not using it.</summary>
    static readonly Dictionary<string, string> BuiltinCaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRINT"] = "terminal", ["INPUT"] = "terminal",
        ["INPUTLINE"] = "terminal", ["INKEY"] = "terminal",

        ["TCPCONNECT"] = "net", ["TRYTCPCONNECT"] = "net", ["TRYTCPREQUEST"] = "net",
        ["TCPLISTEN"] = "net", ["TCPACCEPT"] = "net", ["TCPSEND"] = "net",
        ["TCPRECV"] = "net", ["TCPEOF"] = "net", ["TCPPOLL"] = "net",
        ["TCPPEER"] = "net", ["TCPCLOSE"] = "net", ["TCPSECURE"] = "net",

        ["READFILE"] = "file", ["TRYREADFILE"] = "file", ["TRYREADLINES"] = "file",
        ["WRITEFILE"] = "file",
        ["APPENDFILE"] = "file", ["TRYWRITEFILE"] = "file", ["FILEEXISTS"] = "file",
        ["DELETEFILE"] = "file", ["TRYDELETEFILE"] = "file",
        ["BOPEN"] = "file", ["TRYBOPEN"] = "file", ["BCLOSE"] = "file",
        ["SEEK"] = "file", ["BPOS"] = "file", ["BSIZE"] = "file",
        ["PUTNUM"] = "file", ["GETNUM"] = "file", ["PUTBOOL"] = "file",
        ["GETBOOL"] = "file", ["PUTSTR"] = "file", ["GETSTR"] = "file",

        ["CLOCK"] = "clock", ["TICKS"] = "clock", ["SLEEP"] = "clock",
        ["RND"] = "random", ["SEED"] = "random",
        ["SHUFFLE"] = "random", ["SAMPLE"] = "random",     // draw from RND: random.shoddy's words until lists became cells

        ["SCRIBBLEROPEN"] = "scribbler", ["TRYSCRIBBLEROPEN"] = "scribbler",
        ["SCRIBBLEROF"] = "scribbler", ["SCRIBBLERSHUT"] = "scribbler",
        ["SCRIBBLERPIXEL"] = "scribbler", ["SCRIBBLERFILL"] = "scribbler",
        ["SCRIBBLERTEXT"] = "scribbler", ["SCRIBBLERGETPIXEL"] = "scribbler",
        ["SCRIBBLERWIDTH"] = "scribbler", ["SCRIBBLERHEIGHT"] = "scribbler",
        ["SCRIBBLERBLIT"] = "scribbler", ["SCRIBBLERCLOSE"] = "scribbler",
        ["SCRIBBLERTITLE"] = "scribbler", ["SCRIBBLERPOLL"] = "scribbler",
        ["SCRIBBLERWAIT"] = "scribbler", ["SCRIBBLERSETINTERVAL"] = "scribbler",
        ["SCRIBBLERSAVE"] = "scribbler", ["SCRIBBLERPLACE"] = "scribbler",

        ["SOUND"] = "buzzer", ["NOTEON"] = "buzzer", ["NOTEOFF"] = "buzzer",
        ["SOUNDQUEUE"] = "buzzer", ["SOUNDQUEUED"] = "buzzer",
        ["SOUNDSTOP"] = "buzzer", ["SOUNDGAIN"] = "buzzer", ["SOUNDWAVE"] = "buzzer",
    };

    /// <summary>A machine source's capability tag: the first
    /// `Rem Capability: X` line, or null for a pure machine.</summary>
    public static string? TagOf(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return null;
        foreach (string line in File.ReadLines(sourcePath))
        {
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(
                    line, @"^Rem Capability:\s*([a-z0-9]+)\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>The machine's source, wherever a machine library holds
    /// it now; "" when nowhere, which reads as no tag.</summary>
    static string SourceOf(MachineInfo m) => MachineSet.SourcePathOf(m) ?? "";

    /// <summary>The capabilities a parsed program actually reaches, each
    /// with one example word for the diagnostic.</summary>
    public static Dictionary<string, string> Used(ShoddyProgram prog, MachineSet machines)
    {
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> words = Lint.CalledWords(prog);

        foreach (string w in words)
            if (BuiltinCaps.TryGetValue(w, out string? cap))
                used.TryAdd(cap, w);

        // Machine words: the declaring machine's tag, and whatever the
        // word's own body reaches.
        var reach = new MachineReach(machines);
        foreach ((string name, string target) in prog.ExternalDefs)
        {
            if (!words.Contains(name)) continue;
            foreach (string cap in reach.Of(target))
                used.TryAdd(cap, name);
        }
        return used;
    }

    /// <summary>What a machine word reaches: its machine's tag, plus what
    /// its body calls, read from the machine's source and followed through
    /// the machine's own Defs and into the machines it calls. Sources are
    /// parsed once per machine, tags read once per machine, and each word
    /// answered once. A source that cannot be found or parsed reaches
    /// nothing beyond its tag, as a missing source already reads as no
    /// tag: a lint warns, and never fails the build.</summary>
    sealed class MachineReach
    {
        readonly MachineSet machines;
        readonly Dictionary<string, MachineInfo> byClass = new(StringComparer.Ordinal);
        readonly Dictionary<string, string?> tags = new(StringComparer.Ordinal);
        readonly Dictionary<string, ShoddyProgram?> sources = new(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> answered = new(StringComparer.Ordinal);

        public MachineReach(MachineSet machines)
        {
            this.machines = machines;
            foreach (MachineInfo m in machines.Machines) byClass.TryAdd(m.ClassName, m);
        }

        /// <summary>The capabilities a call target (Class.Method) reaches.</summary>
        public HashSet<string> Of(string target)
        {
            if (answered.TryGetValue(target, out HashSet<string>? known)) return known;
            var caps = new HashSet<string>(StringComparer.Ordinal);
            answered[target] = caps;   // a cycle back here adds nothing new
            int cut = target.LastIndexOf('.');
            if (cut < 0 || !byClass.TryGetValue(target[..cut], out MachineInfo? m)) return caps;
            if (Tag(m) is string tag) caps.Add(tag);
            string method = target[(cut + 1)..];
            foreach ((string def, string id) in m.Defs)
                if (id == method && Source(m) is ShoddyProgram src)
                    Body(src, def, caps, new HashSet<string>(StringComparer.Ordinal));
            return caps;
        }

        /// <summary>One Def's body, and the machine's own Defs it calls.</summary>
        void Body(ShoddyProgram src, string def, HashSet<string> caps, HashSet<string> seen)
        {
            if (!seen.Add(def)) return;
            foreach (string w in Lint.CalledWords(src, def))
            {
                string own = src.ResolveName(w, null);
                if (src.Defs.ContainsKey(own))
                    Body(src, own, caps, seen);
                else if (src.ExternalDefs.TryGetValue(w, out string? target))
                    caps.UnionWith(Of(target));
                else if (BuiltinCaps.TryGetValue(w, out string? cap))
                    caps.Add(cap);
            }
        }

        string? Tag(MachineInfo m)
        {
            if (!tags.TryGetValue(m.ClassName, out string? t))
                tags[m.ClassName] = t = TagOf(SourceOf(m));
            return t;
        }

        /// <summary>The machine's source, parsed as `mill machine` parses
        /// it: its own Includes resolve against the same compiled
        /// libraries, in a set of its own so the caller's is untouched.</summary>
        ShoddyProgram? Source(MachineInfo m)
        {
            if (sources.TryGetValue(m.ClassName, out ShoddyProgram? p)) return p;
            p = null;
            string? path = MachineSet.SourcePathOf(m);
            if (path != null)
            {
                try
                {
                    var set = new MachineSet(machines.Debug, machines.LibraryPaths);
                    List<Line> lines = Lexer.ReadProgram(path, (sb, q) => MachineResolve.Resolve(set, sb, q));
                    var seeded = new ShoddyProgram();
                    set.SeedInto(seeded);
                    p = Parser.Parse(lines, seeded);
                }
                catch (Exception e) when (e is ShoddyError or IOException)
                {
                    p = null;
                }
            }
            sources[m.ClassName] = p;
            return p;
        }
    }

    /// <summary>The lint itself. Empty when no manifest sits beside the
    /// file, or the manifest is unreadable — judging a manifest is `mill
    /// manifest`'s job, not a weave warning's.</summary>
    public static List<string> Run(ShoddyProgram prog, MachineSet machines, string file)
    {
        var warnings = new List<string>();
        string folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        string manifest = Path.Combine(folder, Manifest.FileName);
        if (!File.Exists(manifest)) return warnings;

        HashSet<string> declared;
        bool isWholeMill;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            declared = doc.RootElement.TryGetProperty("capabilities", out JsonElement caps) &&
                       caps.ValueKind == JsonValueKind.Object
                ? caps.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>();
            // The shell splices the core, so weaving it sees the mill's
            // whole usage; a core woven alone sees only its own half.
            string? shell = doc.RootElement.TryGetProperty("shell", out JsonElement sh) &&
                            sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
            isWholeMill = shell == null ||
                string.Equals(Path.GetFileName(file), shell, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return warnings;
        }

        Dictionary<string, string> used = Used(prog, machines);
        // Used-but-undeclared is valid from any of the mill's files: what
        // one file reaches, the mill reaches.
        foreach ((string cap, string via) in used.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!declared.Contains(cap))
                warnings.Add($"capability: {cap} is used ({via}) but not declared in {Manifest.FileName}");
        // Declared-but-unused can only be judged where the whole surface
        // is visible — the shell (or the core of a shell-less mill). A
        // core alone not printing does not make `terminal` unused.
        if (isWholeMill)
            foreach (string cap in declared.OrderBy(c => c, StringComparer.Ordinal))
                if (!used.ContainsKey(cap))
                    warnings.Add($"capability: {cap} is declared in {Manifest.FileName} but nothing reaches it");
        return warnings;
    }
}
