// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// A machine's manifest carries what it declares, and nothing it includes.
///
/// The exported surface used to depend on build state: with the dependency
/// source-spliced, its words were woven into the including machine and
/// manifested under whatever namespace the Include carried; with the
/// dependency already compiled, they became external references and were
/// manifested by nobody. The same source produced two different public
/// interfaces depending on whether a DLL happened to exist.
///
/// These build the same machine both ways and demand one answer.
///
/// Scope: this is about the *manifest*. What a consumer can ultimately
/// name also depends on how transitively-loaded machines are seeded, which
/// is a separate question — a consumer here still sees the dependency's
/// words when they arrive through a DLL reference.
///
/// Each case uses its own machine names: assemblies are loaded by path but
/// resolved by simple name, so reusing one across workspaces in a single
/// test process is a load conflict, not a fresh build.
/// </summary>
[Collection("golden")]
public class MachineSurfaceTests
{
    static readonly string Root = RepoRoot.Dir;

    static string DepSource => """
                               Def DepAvg(xs As List Of Number) As Number
                                   Fold(xs, 0, +) / Length(xs)

                               Type DepBox
                                   Held As Number
                               """;

    static string DemoSource(string dep) => $"""
        Include "{dep}.shoddy" As St

        Def DemoPmt(pv As Number, rate As Number, n As Number) As Number
            pv * rate / (1 - (1 + rate) ^ (0 - n))

        Def DemoAvg(xs As List Of Number) As Number
            StDepAvg(xs)
        """;

    [Theory]
    // dependency compiled first, so demo resolves it as a DLL
    [InlineData(true, "depa", "demoa")]
    // dependency left as source, so demo splices it
    [InlineData(false, "depb", "demob")]
    public void ManifestCarriesOnlyItsOwnDeclarations(bool buildDepFirst, string dep, string demo)
    {
        string machines = Workspace();
        File.WriteAllText(Path.Combine(machines, dep + ".shoddy"), DepSource);
        File.WriteAllText(Path.Combine(machines, demo + ".shoddy"), DemoSource(dep));

        if (buildDepFirst) Build(Path.Combine(machines, dep + ".shoddy"));
        Build(Path.Combine(machines, demo + ".shoddy"));

        MachineInfo info = ManifestOf(Path.Combine(machines, demo + ".shoddy"));

        // Its own words, whichever way the dependency was satisfied.
        Assert.Contains("DEMOPMT", info.Defs.Keys);
        Assert.Contains("DEMOAVG", info.Defs.Keys);

        // Not the dependency's, under any spelling. `As` names a dependency
        // for the includer's convenience; it is not a re-export.
        Assert.DoesNotContain("STDEPAVG", info.Defs.Keys);
        Assert.DoesNotContain("DEPAVG", info.Defs.Keys);
        Assert.DoesNotContain("STDEPBOX", info.Types.Keys);
        Assert.DoesNotContain("DEPBOX", info.Types.Keys);
    }

    /// <summary>Scoping the manifest must not unlink the dependency.
    /// DemoAvg calls into it, so the built DLL must still reference it and
    /// the word must still resolve for a consumer.
    ///
    /// That the call then *runs* is covered end to end by MachineTests,
    /// which weaves libtest against the real machines and compares golden
    /// bytes.</summary>
    [Fact]
    public void DependencyStillLinks()
    {
        string machines = Workspace();
        File.WriteAllText(Path.Combine(machines, "depc.shoddy"), DepSource);
        File.WriteAllText(Path.Combine(machines, "democ.shoddy"), DemoSource("depc"));
        Build(Path.Combine(machines, "depc.shoddy"));
        string demo = Path.Combine(machines, "democ.shoddy");
        Build(demo);

        // The dependency is gone from the manifest but still referenced.
        Assembly built = Assembly.LoadFrom(MachineSet.DllPathFor(demo));
        Assert.Contains("Shoddy.Machines.Depc",
                        built.GetReferencedAssemblies().Select(r => r.Name));

        // And a consumer that includes only demo can still call the word
        // whose body reaches through to it.
        string ws = Path.GetDirectoryName(machines)!;
        string src = Path.Combine(ws, "consumer.shoddy");
        File.WriteAllText(src,
            "Include \"machines/democ.shoddy\"\n\nDef Main()\n    Print(DemoAvg({ 1, 2, 3, 4 }))\n");

        var (prog, set) = ParseWithMachines(src);
        Assert.True(prog.ExternalDefs.ContainsKey("DEMOAVG"));
        Weaver.Weave(prog, Path.Combine(ws, "consumer.dll"), set.Machines);   // links clean
    }

    /// <summary>`Include ... As Q` must mean the same thing whether the
    /// machine is resolved from a DLL or spliced from source. It did not:
    /// spliced, the qualifier reached the dependency's dependencies, so a
    /// program could compile under the runner and fail under the debugger,
    /// which always splices.</summary>
    [Theory]
    [InlineData(true)]      // dependency compiled — resolved as a DLL
    [InlineData(false)]     // dependency not compiled — spliced from source
    public void NamespaceCoversOneLevelOnEitherPath(bool buildDepFirst)
    {
        string machines = Workspace();
        // depd declares DepAvg; deepd, which depd includes bare, declares Deep.
        File.WriteAllText(Path.Combine(machines, "deepd.shoddy"),
            "Def Deep() As Number\n    7\n");
        File.WriteAllText(Path.Combine(machines, "depd.shoddy"),
            "Include \"deepd.shoddy\"\n\n" + DepSource);

        if (buildDepFirst)
        {
            Build(Path.Combine(machines, "deepd.shoddy"));
            Build(Path.Combine(machines, "depd.shoddy"));
        }

        string ws = Path.GetDirectoryName(machines)!;
        string src = Path.Combine(ws, "user.shoddy");
        File.WriteAllText(src,
            "Include \"machines/depd.shoddy\" As St\n\nDef Main()\n    Print(1)\n");
        var (prog, _) = ParseWithMachines(src);

        // The named file's own surface is prefixed...
        Assert.True(Reachable(prog, "STDEPAVG"));
        // ...and what it includes is not, on either path.
        Assert.False(Reachable(prog, "STDEEP"));

        // Whether DEEP is reachable *unprefixed* is a different question
        // and the two paths still disagree about it: seeding scopes a
        // resolved machine's dependencies out, and splicing has no seeding
        // step to do that. Tracked separately; this test is about the
        // namespace, not about visibility.
    }

    /// <summary>A name the program can call, wherever it came from: a
    /// spliced include lands in Defs, a resolved machine in ExternalDefs.</summary>
    static bool Reachable(ShoddyProgram prog, string name) =>
        prog.Defs.ContainsKey(name) || prog.ExternalDefs.ContainsKey(name);

    /// <summary>A machine's includes are its own business: a consumer
    /// names what it included, and not what that machine included in
    /// turn. Dependencies still load and link — only naming is scoped.</summary>
    [Fact]
    public void ADependencyOfAMachineIsNotNameable()
    {
        string machines = Workspace();
        File.WriteAllText(Path.Combine(machines, "deepe.shoddy"),
            """
            Def Deep() As Number
                7

            Type DeepBox
                Held As Number
            """);
        File.WriteAllText(Path.Combine(machines, "depe.shoddy"),
            """
            Include "deepe.shoddy"

            Def DepUse() As Number
                Deep()
            """);
        Build(Path.Combine(machines, "deepe.shoddy"));
        Build(Path.Combine(machines, "depe.shoddy"));

        string ws = Path.GetDirectoryName(machines)!;
        string src = Path.Combine(ws, "user.shoddy");
        File.WriteAllText(src,
            """
            Include "machines/depe.shoddy"

            Def Main()
                Print(DepUse())
            """);
        var (prog, _) = ParseWithMachines(src);

        Assert.True(prog.ExternalDefs.ContainsKey("DEPUSE"));    // what it included
        Assert.False(prog.ExternalDefs.ContainsKey("DEEP"));     // what that included
        Assert.False(prog.ExternalTypes.ContainsKey("DEEPBOX"));

        // Still linked, though: DepUse's body calls Deep.
        Assert.Contains(prog.Unseeded, e => e.Key == "DEEP");
    }

    /// <summary>The refusal names the machine to add. Without this the
    /// rule reads as an arbitrary rejection and every reader has to learn
    /// the include graph by hand.</summary>
    [Fact]
    public void TheRefusalNamesTheIncludeToAdd()
    {
        string machines = Workspace();
        File.WriteAllText(Path.Combine(machines, "deepf.shoddy"),
            """
            Def DeepWord() As Number
                7
            """);
        File.WriteAllText(Path.Combine(machines, "depf.shoddy"),
            """
            Include "deepf.shoddy"

            Def DepUse() As Number
                DeepWord()
            """);
        Build(Path.Combine(machines, "deepf.shoddy"));
        Build(Path.Combine(machines, "depf.shoddy"));

        string ws = Path.GetDirectoryName(machines)!;
        string src = Path.Combine(ws, "user.shoddy");
        File.WriteAllText(src,
            """
            Include "machines/depf.shoddy"

            Def Main()
                Print(DeepWord())
            """);
        var (prog, _) = ParseWithMachines(src);
        var lines = Lexer.ReadProgram(src, new MachineSet().TryResolve);
        Lint.Result r = Lint.Run(prog, lines);

        string msg = Assert.Single(r.UnknownWords).Item1;
        Assert.Contains("DEEPWORD", msg);
        Assert.Contains("declared in deepf.shoddy", msg);
        Assert.Contains("depf.shoddy includes but does not export", msg);
        Assert.Contains("Add: Include \"deepf.shoddy\"", msg);
    }

    /// <summary>The debugger builds the same program the runner does.
    /// It used to splice every machine, which meant none of the scoping
    /// applied under it: a program the runner refused would launch and
    /// run. This drives the debug-adapter pipeline — resolve machines,
    /// lint, weave — and demands the refusal.</summary>
    [Fact]
    public void TheDebugPipelineEnforcesTheSameScoping()
    {
        string machines = Workspace();
        File.WriteAllText(Path.Combine(machines, "deepg.shoddy"),
            """
            Def DeepWord() As Number
                7
            """);
        File.WriteAllText(Path.Combine(machines, "depg.shoddy"),
            """
            Include "deepg.shoddy"

            Def DepUse() As Number
                DeepWord()
            """);
        Build(Path.Combine(machines, "deepg.shoddy"));
        Build(Path.Combine(machines, "depg.shoddy"));

        string ws = Path.GetDirectoryName(machines)!;
        string src = Path.Combine(ws, "user.shoddy");
        File.WriteAllText(src,
            """
            Include "machines/depg.shoddy"

            Def Main()
                Print(DeepWord())
            """);

        var set = new MachineSet();
        var lines = Lexer.ReadProgram(src, set.TryResolve);
        var seeded = new ShoddyProgram();
        set.SeedInto(seeded);
        ShoddyProgram prog = Parser.Parse(lines, seeded);

        string msg = Assert.Single(Lint.Run(prog, lines).UnknownWords).Item1;
        Assert.Contains("Add: Include \"deepg.shoddy\"", msg);
    }

    /// <summary>The manifest of one built machine, read back from its DLL.</summary>
    static MachineInfo ManifestOf(string sbPath)
    {
        string dll = MachineSet.DllPathFor(sbPath);
        return MachineInfo.From(Assembly.LoadFrom(dll), dll);
    }

    /// <summary>A scratch machines/ directory. Nothing is copied in: these
    /// tests build only their own two files, so they never contend with
    /// another test over a standard machine's assembly name.</summary>
    static string Workspace()
    {
        string machines = Path.Combine(Path.GetTempPath(), "shoddy-surface",
                                       Guid.NewGuid().ToString("N"), "machines");
        Directory.CreateDirectory(machines);
        return machines;
    }

    static void Build(string sbPath)
    {
        var (prog, machines) = ParseWithMachines(sbPath);
        Weaver.WeaveMachine(prog, sbPath, machines.Machines);
    }

    /// <summary>The mill's own pipeline, including the onQualified hook —
    /// without it an `Include ... As` is silently dropped and these tests
    /// would pass by testing nothing.</summary>
    static (ShoddyProgram, MachineSet) ParseWithMachines(string file)
    {
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(file, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        return (Parser.Parse(lines, prog), machines);
    }
}
