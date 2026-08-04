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
        var lines = Lexer.ReadProgram(file, machines.TryResolve, machines.SetQualifier);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        return (Parser.Parse(lines, prog), machines);
    }
}
