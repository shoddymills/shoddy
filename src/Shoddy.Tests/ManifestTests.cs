// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text.Json.Nodes;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// `mill manifest` and the capability derivation.
///
/// The machines section is generated, never hand-maintained: --write
/// derives the transitive closure from the same resolver every verb
/// uses, verification regenerates and diffs, and a hand-staled section
/// fails. The structural checks are the toolchain's own validation —
/// the published JSON Schema mirrors them for editors, and the shipped
/// halifax manifest is validated here against the code so schema, code
/// and example cannot drift apart silently.
///
/// (verify-host-blind: host names are the subject here — a fixture below
/// uses one precisely to prove the capability vocabulary refuses host
/// concepts.)
/// </summary>
[Collection("golden")]
public class ManifestTests
{
    static readonly string Root = RepoRoot.Dir;

    const string DepSource = """
        Def ManAvg(xs As List Of Number) As Number
            Fold(xs, 0, +) / Length(xs)
        """;

    /// <summary>A fresh mill folder: a shell (with a machine include and
    /// a Print), a core, and a test.shoddy golden.</summary>
    static string Mill(string dep)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-manifest",
                                 Guid.NewGuid().ToString("N"));
        string machines = Path.Combine(ws, "machines");
        Directory.CreateDirectory(machines);
        File.WriteAllText(Path.Combine(machines, dep + ".shoddy"), DepSource);
        // Built, so the resolver satisfies the Include from a DLL — a
        // scratch folder is not a machine library, and only a DLL beside
        // the source makes it a machine rather than a splice.
        Build(Path.Combine(machines, dep + ".shoddy"));
        string mill = Path.Combine(ws, "widget");
        Directory.CreateDirectory(mill);
        File.WriteAllText(Path.Combine(mill, "widget-core.shoddy"),
            $"Include \"../machines/{dep}.shoddy\"\n\n" +
            "Def WidgetScore(xs As List Of Number) As Number\n    ManAvg(xs) * 2\n");
        File.WriteAllText(Path.Combine(mill, "widget.shoddy"),
            "Include \"widget-core.shoddy\"\n\nDef Main()\n    Print(WidgetScore({ 1, 2, 3 }))\n");
        File.WriteAllText(Path.Combine(mill, "test.shoddy"),
            "Include \"widget-core.shoddy\"\n\nDef Main()\n    Assert(WidgetScore({ 2, 4 }) = 6, \"SCORE\")\n");
        return mill;
    }

    [Fact]
    public void WriteGeneratesVerifyPassesStaleFails()
    {
        string mill = Mill("mana");
        string manifest = Path.Combine(mill, Manifest.FileName);

        // Only a machine-library file resolves as a machine; the
        // fixture's machines/ becomes the library the way a real
        // checkout's does.
        string? saved = Environment.GetEnvironmentVariable("SHODDYLIB");
        Environment.SetEnvironmentVariable("SHODDYLIB",
            Path.Combine(Path.GetDirectoryName(mill)!, "machines"));
        try
        {

        // --write creates the skeleton: convention-named files found,
        // the closure derived through the resolver.
        Assert.Equal(0, Manifest.Run(mill, write: true));
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal("widget", (string)root["name"]!);
        Assert.Equal("widget-core.shoddy", (string)root["core"]!);
        Assert.Equal("widget.shoddy", (string)root["shell"]!);
        Assert.Equal(new[] { "mana" },
            root["machines"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(new[] { "test.shoddy" },
            root["goldens"]!.AsArray().Select(n => (string)n!).ToArray());

        // Fresh manifest verifies clean.
        Assert.Equal(0, Manifest.Run(mill, write: false));

        // A hand-edited stale dependency section fails verification.
        root["machines"] = new JsonArray();
        File.WriteAllText(manifest, root.ToJsonString());
        Assert.Equal(1, Manifest.Run(mill, write: false));

        // And --write repairs it, preserving the hand-authored fields.
        root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
        root["capabilities"] = new JsonObject { ["clock"] = new JsonObject { ["optional"] = true } };
        File.WriteAllText(manifest, root.ToJsonString());
        Assert.Equal(0, Manifest.Run(mill, write: true));
        root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal(new[] { "mana" },
            root["machines"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.True((bool)root["capabilities"]!["clock"]!["optional"]!);
        Assert.Equal(0, Manifest.Run(mill, write: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHODDYLIB", saved);
        }
    }

    [Fact]
    public void StructuralChecksRefuseTheBroken()
    {
        string mill = Mill("manb");
        string manifest = Path.Combine(mill, Manifest.FileName);
        Assert.Equal(0, Manifest.Run(mill, write: true));
        var good = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;

        void Broken(Action<JsonObject> breakIt)
        {
            var copy = (JsonObject)JsonNode.Parse(good.ToJsonString())!;
            breakIt(copy);
            File.WriteAllText(manifest, copy.ToJsonString());
            Assert.Equal(1, Manifest.Run(mill, write: false));
        }

        Broken(m => m.Remove("modes"));                       // modes required
        Broken(m => m.Remove("core"));                        // mode N needs a core
        Broken(m => m["capabilities"] = new JsonObject { ["azure"] = true });   // vocabulary only
        Broken(m => m["capabilities"] = new JsonObject { ["net"] = "yes" });    // true or object
        Broken(m => m["capabilities"] = new JsonObject
            { ["buzzer"] = new JsonObject { ["read"] = new JsonArray("x") } }); // read/write are file's
        Broken(m => m["goldens"] = new JsonArray("missing.shoddy"));            // goldens must exist
        Broken(m => m["extras"] = true);                       // unknown field
    }

    /// <summary>The shipped example manifest stays valid against the
    /// structural checks and its generated section stays true — the
    /// drift test D1 demands. Out of process, through the real mill:
    /// walking halifax loads dozens of machine assemblies, and a test
    /// process that also builds scratch machines under the same simple
    /// names must not share a load context with them.</summary>
    [Fact]
    public void ShippedHalifaxManifestVerifies()
    {
        string millExe = Path.Combine(Root, "bin", "mill.exe");
        if (!File.Exists(millExe)) millExe = Path.Combine(Root, "bin", "mill");
        var psi = new System.Diagnostics.ProcessStartInfo(millExe,
            $"manifest \"{Path.Combine(Root, "mills", "halifax")}\"")
        { RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    static void Build(string sbPath)
    {
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sbPath, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sbPath, machines.Machines);
    }

    // ================= the capability derivation =================

    [Fact]
    public void MachineTagsAreReadTextually()
    {
        Assert.Equal("buzzer", CapabilityLint.TagOf(Path.Combine(Root, "machines", "buzzer.shoddy")));
        Assert.Equal("net", CapabilityLint.TagOf(Path.Combine(Root, "machines", "https.shoddy")));
        Assert.Equal("file", CapabilityLint.TagOf(Path.Combine(Root, "machines", "seeds", "seedisam.shoddy")));
        Assert.Null(CapabilityLint.TagOf(Path.Combine(Root, "machines", "seq.shoddy")));   // pure
    }

    [Fact]
    public void UndeclaredUsedAndDeclaredUnusedEachWarn()
    {
        string mill = Mill("manc");
        // The shell prints (terminal) and reads a file (file, via a
        // builtin); the manifest declares terminal and clock only.
        File.WriteAllText(Path.Combine(mill, "widget.shoddy"),
            "Def Main()\n    Print(TryReadFile(\"in.txt\"))\n");
        File.WriteAllText(Path.Combine(mill, Manifest.FileName),
            """
            {
              "name": "widget",
              "shell": "widget.shoddy",
              "modes": ["T"],
              "machines": [],
              "capabilities": { "terminal": true, "clock": { "optional": true } }
            }
            """);

        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(Path.Combine(mill, "widget.shoddy"),
                                             (p, q) => MachineResolve.Resolve(machines, p, q));
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);

        List<string> w = CapabilityLint.Run(parsed, machines, Path.Combine(mill, "widget.shoddy"));
        Assert.Contains(w, x => x.Contains("file is used") && x.Contains("not declared"));
        Assert.Contains(w, x => x.Contains("clock is declared") && x.Contains("nothing reaches it"));
        Assert.DoesNotContain(w, x => x.Contains("terminal"));
    }

    /// <summary>A core woven alone must not be told the mill's terminal
    /// declaration is unused — only the shell sees the whole surface.</summary>
    [Fact]
    public void DeclaredUnusedIsJudgedOnlyAtTheShell()
    {
        string mill = Mill("mand");
        File.WriteAllText(Path.Combine(mill, Manifest.FileName),
            """
            {
              "name": "widget",
              "core": "widget-core.shoddy",
              "shell": "widget.shoddy",
              "modes": ["T", "N"],
              "machines": ["mand"],
              "capabilities": { "terminal": true }
            }
            """);
        string core = Path.Combine(mill, "widget-core.shoddy");
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(core, (p, q) => MachineResolve.Resolve(machines, p, q));
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);

        Assert.Empty(CapabilityLint.Run(parsed, machines, core));
    }
}
