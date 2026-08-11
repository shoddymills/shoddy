// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;

namespace Shoddy.Tests;

/// <summary>
/// The proofs/ tree, run as C8 demands: the C# and F# console consumers
/// pass; the ungranted fixture is refused at configure time with the
/// self-documenting message; the degraded fixture builds with warnings
/// and runs on null seams; and the floor consumer's output contains the
/// dependency floor and nothing above it — asserted by name, never
/// inspected.
///
/// These launch real dotnet builds over the real mills, so they carry
/// the "golden" collection's serialization: the mill bin and machine
/// DLLs are shared state.
/// </summary>
[Collection("golden")]
public class ProofTests
{
    static readonly string Root = RepoRoot.Dir;

    static (int Exit, string Out) Dotnet(string args, string workdir)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workdir,
        };
        return ProcessRun.CaptureAll(psi);
    }

    /// <summary>The published mill the proof projects' ShoddyToolPath
    /// names; built once if absent (a fresh checkout under plain
    /// `dotnet test`).</summary>
    static ProofTests()
    {
        if (!File.Exists(Path.Combine(Root, "bin", "mill.exe")) &&
            !File.Exists(Path.Combine(Root, "bin", "mill")))
        {
            // The nested publish the build scripts warn about: on a cold
            // cache it is slow AND loud, and reading its pipes one at a time
            // is what turned "slow" into "stopped".
            var psi = new ProcessStartInfo("dotnet",
                $"publish src/Shoddy.Mill -c Release -o bin")
            { WorkingDirectory = Root };
            ProcessRun.CaptureAll(psi);
        }
    }

    /// <summary>The published mill, by the name this OS gives it —
    /// bin/mill.exe on Windows, bin/mill everywhere else. The scaffold
    /// builds pass it as ShoddyToolPath; naming the .exe unconditionally
    /// broke every proof on the Linux runners (v2.0.0).</summary>
    static string MillPath => Path.Combine(Root, "bin",
        OperatingSystem.IsWindows() ? "mill.exe" : "mill");

    [Fact]
    public void ConsoleCsProofsPass()
    {
        var (exit, output) = Dotnet("run --project proofs/console-cs", Root);
        Assert.True(exit == 0, output);
        Assert.Contains("ALL PROOFS PASSED", output);
    }

    [Fact]
    public void ConsoleFsProofsPass()
    {
        var (exit, output) = Dotnet("run --project proofs/console-fs", Root);
        Assert.True(exit == 0, output);
        Assert.Contains("ALL PROOFS PASSED", output);
    }

    /// <summary>The refusal, and its text: default-deny is only humane
    /// when the error names the capability, the mill, the manifest and
    /// the exact line to add (C2.4b) — so the message is asserted, not
    /// just the failure.</summary>
    [Fact]
    public void UngrantedRequiredCapabilityRefusesTheBuild()
    {
        var (exit, output) = Dotnet("build proofs/negative/refused/refused.csproj", Root);
        Assert.NotEqual(0, exit);
        Assert.Contains("SHODDY1002", output);
        Assert.Contains("'halifax'", output);
        Assert.Contains("capability 'file'", output);
        Assert.Contains("Grant=\"file\"", output);
        Assert.Contains("mill.manifest", output);
    }

    [Fact]
    public void UngrantedOptionalCapabilityWarnsAndDegrades()
    {
        var (buildExit, buildOut) = Dotnet("build proofs/negative/degraded/degraded.csproj", Root);
        Assert.True(buildExit == 0, buildOut);
        Assert.Contains("SHODDY1003", buildOut);
        Assert.Contains("'scribbler'", buildOut);

        var (runExit, runOut) = Dotnet("run --project proofs/negative/degraded --no-build", Root);
        Assert.True(runExit == 0, runOut);
        Assert.Contains("42", runOut);
    }

    /// <summary>C6.2 + C6.5 (B7.4 for Part C): the debug configuration
    /// carries the Perch attach service; the release configuration
    /// provably carries neither the transport nor the instrumented
    /// core — asserted on the built outputs, not the docs.</summary>
    [Fact]
    public void DebugCarriesThePerchAndReleaseIsClean()
    {
        var (dbgExit, dbgOut) = Dotnet("build proofs/console-cs -c Debug", Root);
        Assert.True(dbgExit == 0, dbgOut);
        string dbgDir = Path.Combine(Root, "artifacts", "bin", "console-cs", "debug");
        Assert.Contains("Shoddy.Perch.dll",
            Directory.GetFiles(dbgDir, "*.dll").Select(Path.GetFileName));

        var (relExit, relOut) = Dotnet("build proofs/console-cs -c Release", Root);
        Assert.True(relExit == 0, relOut);
        string relDir = Path.Combine(Root, "artifacts", "bin", "console-cs", "release");
        Assert.DoesNotContain("Shoddy.Perch.dll",
            Directory.GetFiles(relDir, "*.dll").Select(Path.GetFileName));
    }

    /// <summary>C7.2/C7.4: every graduation package packs, today, from
    /// the tree as it stands — so publication, when its trigger arrives
    /// (the first consumer outside this repo, C7.3), is a packaging
    /// step and not a restructuring. Publication is NOT performed.</summary>
    [Fact]
    public void GraduationPackagesPack()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "shoddy-pack", Guid.NewGuid().ToString("N"));
        foreach (string proj in new[]
        {
            "src/Shoddy.Runtime", "src/Shoddy.Hosting",
            "src/Shoddy.Build", "src/Shoddy.Mill.Headless",
        })
        {
            var (exit, output) = Dotnet($"pack {proj} -c Release -o \"{outDir}\"", Root);
            Assert.True(exit == 0, $"{proj}: {output}");
        }
        string[] packages = Directory.GetFiles(outDir, "*.nupkg").Select(Path.GetFileName).ToArray()!;
        Assert.Contains(packages, p => p!.StartsWith("Shoddy.Runtime."));
        Assert.Contains(packages, p => p!.StartsWith("Shoddy.Hosting."));
        Assert.Contains(packages, p => p!.StartsWith("Shoddy.Build."));
        Assert.Contains(packages, p => p!.StartsWith("Shoddy.Mill.Headless."));
    }

    /// <summary>C5/C8: the templates scaffold projects that build from a
    /// bare SDK — installed from the tree, pointed at the pure fixture
    /// mill, built, and (for the console) run.</summary>
    [Fact]
    public void TemplatesScaffoldAndBuild()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-tpl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try
        {
            foreach (string tpl in new[] { "templates/shoddy-console", "templates/shoddy-lib" })
                Assert.Equal(0, Dotnet($"new install \"{Path.Combine(Root, tpl)}\"", ws).Exit);

            string mill = Path.Combine(Root, "proofs", "fixtures", "pure");
            string build = Path.Combine(Root, "src", "Shoddy.Build", "build");
            var (nExit, nOut) = Dotnet(
                $"new shoddy-console -n Scaffolded --mill \"{mill}\" --shoddyBuild \"{build}\"" +
                $" -o scaffold-console", ws);
            Assert.True(nExit == 0, nOut);
            var (bExit, bOut) = Dotnet(
                $"build scaffold-console -p:ShoddyToolPath=\"{MillPath}\"", ws);
            Assert.True(bExit == 0, bOut);

            var (lExit, lOut) = Dotnet(
                $"new shoddy-lib -n ScaffoldedLib --mill \"{mill}\" --shoddyBuild \"{build}\"" +
                $" -o scaffold-lib", ws);
            Assert.True(lExit == 0, lOut);
            var (lbExit, lbOut) = Dotnet(
                $"build scaffold-lib -p:ShoddyToolPath=\"{MillPath}\"", ws);
            Assert.True(lbExit == 0, lbOut);
        }
        finally
        {
            Dotnet("new uninstall " + Path.Combine(Root, "templates", "shoddy-console"), ws);
            Dotnet("new uninstall " + Path.Combine(Root, "templates", "shoddy-lib"), ws);
        }
    }

    /// <summary>C1.3, the point of Part C: the capability-free mill's
    /// consumer links the floor and nothing above it.</summary>
    [Fact]
    public void DependencyFloorHoldsByName()
    {
        var (exit, output) = Dotnet("build proofs/floor/floor.csproj", Root);
        Assert.True(exit == 0, output);

        // The repo's Directory.Build.props centralizes every project's
        // output under artifacts/.
        string outDir = Path.Combine(Root, "artifacts", "bin", "floor", "debug");
        string[] dlls = Directory.GetFiles(outDir, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToArray()!;

        // The floor is present...
        Assert.Contains("Shoddy.Runtime.dll", dlls);
        Assert.Contains("Shoddy.Hosting.dll", dlls);
        Assert.Contains("Shoddy.Machines.Pure-core.dll", dlls);

        // ...and nothing above it: no windowing, no audio, no compiler.
        string[] forbidden = { "Silk.NET", "OpenAL", "glfw", "Microsoft.CodeAnalysis",
                               "Shoddy.Compiler", "Shoddy.Devil", "Shoddy.Mill" };
        foreach (string dll in dlls)
            foreach (string f in forbidden)
                Assert.False(dll!.Contains(f, StringComparison.OrdinalIgnoreCase),
                    $"the dependency floor is breached: {dll} in {outDir}");

        // And the program the floor carries actually runs.
        var (runExit, runOut) = Dotnet("run --project proofs/floor --no-build", Root);
        Assert.True(runExit == 0, runOut);
        Assert.Contains("PASS", runOut);
    }
}
