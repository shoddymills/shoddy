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
/// two mechanical sources:
///   1. machines carry a capability tag — a `Rem Capability: X` header
///      line, read textually the way docs ground truth is read;
///   2. a builtin→capability table below, for capabilities that are
///      engine builtins rather than machines.
/// Three consumers read the same derivation: this lint, `mill manifest`,
/// and the ShoddyWeave gate.
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

        ["READFILE"] = "file", ["TRYREADFILE"] = "file", ["WRITEFILE"] = "file",
        ["APPENDFILE"] = "file", ["TRYWRITEFILE"] = "file", ["FILEEXISTS"] = "file",
        ["DELETEFILE"] = "file", ["TRYDELETEFILE"] = "file",
        ["BOPEN"] = "file", ["TRYBOPEN"] = "file", ["BCLOSE"] = "file",
        ["SEEK"] = "file", ["BPOS"] = "file", ["BSIZE"] = "file",
        ["PUTNUM"] = "file", ["GETNUM"] = "file", ["PUTBOOL"] = "file",
        ["GETBOOL"] = "file", ["PUTSTR"] = "file", ["GETSTR"] = "file",

        ["CLOCK"] = "clock", ["TICKS"] = "clock", ["SLEEP"] = "clock",
        ["RND"] = "random", ["SEED"] = "random",

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

    /// <summary>The machine source beside its DLL: bin/Shoddy.Machines.X.dll
    /// sits in a bin/ subdirectory beside x.shoddy, and the assembly stem
    /// round-trips to the source name exactly (split identities).</summary>
    static string SourceOf(MachineInfo m)
    {
        string stem = m.AssemblyName.Replace("Shoddy.Machines.", "").ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(m.DllPath) ?? "", "..", stem + ".shoddy");
    }

    /// <summary>The capabilities a parsed program actually reaches, each
    /// with one example word for the diagnostic.</summary>
    public static Dictionary<string, string> Used(ShoddyProgram prog, MachineSet machines)
    {
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> words = Lint.UsedWords(prog);

        foreach (string w in words)
            if (BuiltinCaps.TryGetValue(w, out string? cap))
                used.TryAdd(cap, w);

        // Machine words: the seeded name's declaring machine carries the
        // tag. Tags are cached per machine — most words share one.
        var tagByClass = new Dictionary<string, string?>();
        foreach (MachineInfo m in machines.Machines)
            tagByClass[m.ClassName] = TagOf(SourceOf(m));
        foreach ((string name, string target) in prog.ExternalDefs)
        {
            if (!words.Contains(name)) continue;
            int cut = target.LastIndexOf('.');
            if (cut < 0) continue;
            if (tagByClass.TryGetValue(target[..cut], out string? cap) && cap != null)
                used.TryAdd(cap, name);
        }
        return used;
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
