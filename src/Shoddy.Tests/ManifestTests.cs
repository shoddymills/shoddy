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
        // Built, so the resolver can satisfy the Include from a compiled
        // machine: the fixture's machines/ is named as the library (the
        // test sets SHODDYLIB), and its bin/ holds what was built.
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
        Assert.Equal(0, Manifest.Run(mill, write: true, Libs(mill)));
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal("widget", (string)root["name"]!);
        Assert.Equal("widget-core.shoddy", (string)root["core"]!);
        Assert.Equal("widget.shoddy", (string)root["shell"]!);
        Assert.Equal(new[] { "mana" },
            root["machines"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(new[] { "test.shoddy" },
            root["goldens"]!.AsArray().Select(n => (string)n!).ToArray());

        // Fresh manifest verifies clean.
        Assert.Equal(0, Manifest.Run(mill, write: false, Libs(mill)));

        // A hand-edited stale dependency section fails verification.
        root["machines"] = new JsonArray();
        File.WriteAllText(manifest, root.ToJsonString());
        Assert.Equal(1, Manifest.Run(mill, write: false, Libs(mill)));

        // And --write repairs it, preserving the hand-authored fields.
        root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
        root["capabilities"] = new JsonObject { ["clock"] = new JsonObject { ["optional"] = true } };
        File.WriteAllText(manifest, root.ToJsonString());
        Assert.Equal(0, Manifest.Run(mill, write: true, Libs(mill)));
        root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal(new[] { "mana" },
            root["machines"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.True((bool)root["capabilities"]!["clock"]!["optional"]!);
        Assert.Equal(0, Manifest.Run(mill, write: false, Libs(mill)));
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
        Assert.Equal(0, Manifest.Run(mill, write: true, Libs(mill)));
        var good = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;

        void Broken(Action<JsonObject> breakIt)
        {
            var copy = (JsonObject)JsonNode.Parse(good.ToJsonString())!;
            breakIt(copy);
            File.WriteAllText(manifest, copy.ToJsonString());
            Assert.Equal(1, Manifest.Run(mill, write: false, Libs(mill)));
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
    /// walking halifax loads the standard library, and a test process
    /// that also builds scratch machines must not share a load context
    /// with it. The mill is this test host's own copy, which carries the
    /// library and its sources beside it.</summary>
    [Fact]
    public void ShippedHalifaxManifestVerifies()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
            $"\"{TestHost.MillDll}\" manifest \"{Path.Combine(Root, "mills", "halifax")}\"");
        // Halifax reaches its machines by relative path into the
        // checkout's machines/, which is the library only when named: a
        // checkout's build names it the same way.
        psi.Environment["SHODDYLIB"] = Path.Combine(Root, "machines");
        psi.Environment.Remove("SHODDYLIBBIN");
        var (exit, text) = ProcessRun.CaptureAll(psi);
        Assert.True(exit == 0, text);
    }

    /// <summary>Weave a machine to bin/ beside its source, as `mill
    /// machine` does.</summary>
    static void Build(string sbPath)
    {
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sbPath, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sbPath, MillVerbs.MachineOut(sbPath, null, false), machines.Machines);
    }

    /// <summary>The compiled machines a fixture mill resolves against:
    /// whatever its machines/bin holds.</summary>
    static IEnumerable<string> Libs(string mill)
    {
        string bin = Path.Combine(Path.GetDirectoryName(mill)!, "machines", "bin");
        return Directory.Exists(bin) ? Directory.GetFiles(bin, "*.dll") : Array.Empty<string>();
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

    /// <summary>A parameter or local that shares a builtin's name is not a
    /// call to the builtin. mungo-caverns was told it used `random` because
    /// NewGame takes a `seed`; it never calls Seed. A real call beside the
    /// shadowed names must still warn.</summary>
    [Fact]
    public void LocalsNamedLikeBuiltinsReachNoCapability()
    {
        string mill = Mill("mane");
        File.WriteAllText(Path.Combine(mill, "widget.shoddy"),
            "Def Roll(seed As Number) As Number\n" +
            "    Let clock = seed + 1\n" +
            "    clock * 2\n" +
            "\n" +
            "Def Main()\n" +
            "    Print(Roll(3))\n" +
            "    Print(TryReadFile(\"in.txt\"))\n");
        File.WriteAllText(Path.Combine(mill, Manifest.FileName),
            """
            {
              "name": "widget",
              "shell": "widget.shoddy",
              "modes": ["T"],
              "machines": [],
              "capabilities": { "terminal": true }
            }
            """);

        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(Path.Combine(mill, "widget.shoddy"),
                                             (p, q) => MachineResolve.Resolve(machines, p, q));
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);

        List<string> w = CapabilityLint.Run(parsed, machines, Path.Combine(mill, "widget.shoddy"));
        Assert.DoesNotContain(w, x => x.Contains("random"));
        Assert.DoesNotContain(w, x => x.Contains("clock"));
        Assert.Contains(w, x => x.Contains("file is used") && x.Contains("not declared"));
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

    /// <summary>A machine word reaches what its own body calls. The
    /// fixture machine carries no tag, and its ManDie reaches RND only
    /// through ManRoll, another of its Defs: the shape of math's Pick and
    /// RandInt, which made devils-dust and invaders look as if they drew no
    /// random numbers. The word is resolved from the compiled machine, so
    /// its body is not in the program and only the machine's source can
    /// say what it reaches.</summary>
    [Fact]
    public void MachineWordsReachWhatTheirBodiesCall()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-manifest", Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(ws, "machines");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "manr.shoddy"),
            "Def ManRoll() As Number\n    Rnd() * 6\n\n" +
            "Def ManDie() As Number\n    Floor(ManRoll()) + 1\n\n" +
            "Def ManHalf(n As Number) As Number\n    n / 2\n");
        Build(Path.Combine(lib, "manr.shoddy"));
        string mill = Path.Combine(ws, "widget");
        Directory.CreateDirectory(mill);
        string shell = Path.Combine(mill, "widget.shoddy");
        File.WriteAllText(shell, "Include \"../machines/manr.shoddy\"\n\nDef Main()\n    Print(ManDie())\n");

        string? saved = Environment.GetEnvironmentVariable("SHODDYLIB");
        Environment.SetEnvironmentVariable("SHODDYLIB", lib);
        try
        {
            List<string> LintWith(string caps)
            {
                File.WriteAllText(Path.Combine(mill, Manifest.FileName),
                    "{ \"name\": \"widget\", \"shell\": \"widget.shoddy\", \"modes\": [\"T\"], " +
                    "\"machines\": [\"manr\"], \"capabilities\": { " + caps + " } }");
                var machines = new MachineSet(false, Libs(mill));
                List<Line> lines = Lexer.ReadProgram(shell, (p, q) => MachineResolve.Resolve(machines, p, q));
                var prog = new ShoddyProgram();
                machines.SeedInto(prog);
                ShoddyProgram parsed = Parser.Parse(lines, prog);
                Assert.False(parsed.Defs.ContainsKey("MANROLL"), "the machine was spliced, not resolved compiled");
                return CapabilityLint.Run(parsed, machines, shell);
            }

            Assert.Contains(LintWith("\"terminal\": true"),
                x => x.Contains("random is used") && x.Contains("not declared"));
            Assert.DoesNotContain(LintWith("\"terminal\": true, \"random\": true"),
                x => x.Contains("random"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHODDYLIB", saved);
        }
    }

    /// <summary>Every shipped mill declares exactly the capabilities it
    /// reaches: no capability warning from its shell, its core or its
    /// goldens. Nothing held this before, so devils-dust and invaders
    /// shipped without `random`, and the MAUI gate grants from what a
    /// manifest declares. Out of process through the real mill, for the
    /// reason ShippedHalifaxManifestVerifies gives. A program (the shell, a
    /// golden) is linted by `mill gen`, which writes no file; the shell
    /// splices its core, so a core is linted alone only when there is no
    /// shell, and then by `mill machine` into a temporary folder, because a
    /// core has no Main to generate.</summary>
    [Fact]
    public void ShippedManifestsDeclareWhatTheMillsReach()
    {
        var found = new List<string>();
        foreach (string mill in Directory.GetDirectories(Path.Combine(Root, "mills")).OrderBy(d => d, StringComparer.Ordinal))
        {
            string manifest = Path.Combine(mill, Manifest.FileName);
            if (!File.Exists(manifest)) continue;
            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!;
            var lints = new List<(string File, string Args)>();
            string? shell = root["shell"]?.GetValue<string>();
            string? core = root["core"]?.GetValue<string>();
            if (shell != null)
                lints.Add((shell, $"gen \"{Path.Combine(mill, shell)}\""));
            else if (core != null)
                lints.Add((core, $"machine \"{Path.Combine(mill, core)}\" -o \"" +
                    Path.Combine(Path.GetTempPath(), "shoddy-manifest", Guid.NewGuid().ToString("N")) + "\""));
            if (root["goldens"] is JsonArray goldens)
                foreach (JsonNode? g in goldens)
                    if (g?.GetValue<string>() is string golden)
                        lints.Add((golden, $"gen \"{Path.Combine(mill, golden)}\""));
            foreach ((string file, string args) in lints)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{TestHost.MillDll}\" {args}");
                psi.Environment["SHODDYLIB"] = Path.Combine(Root, "machines");
                psi.Environment.Remove("SHODDYLIBBIN");
                var (exit, text) = ProcessRun.CaptureAll(psi);
                Assert.True(exit == 0, $"{Path.GetFileName(mill)}/{file}:\n{text}");
                foreach (string line in text.Split('\n'))
                    if (line.TrimStart().StartsWith("capability: ", StringComparison.Ordinal))
                        found.Add($"{Path.GetFileName(mill)}/{file}: {line.Trim()}");
            }
        }
        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
