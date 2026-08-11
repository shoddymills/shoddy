// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;

namespace Shoddy.Tests;

/// <summary>
/// One mill, two builds (C3). The full mill and the headless build share
/// their verb dispatch, so for every verb both carry they must produce
/// byte-identical artifacts (C3.4) — which is also what makes PATH
/// ambiguity harmless (C3.8). And the headless build's `run` and `dap`
/// refuse by name, naming the full mill, rather than answering "unknown
/// command" (C3.3); `--version` is the one place the builds distinguish
/// themselves (C3.6).
///
/// These tests run the two entry points as real processes over the same
/// sources — the only test a user can apply.
/// </summary>
[Collection("golden")]
public class ToolchainEquivalenceTests
{
    static readonly string Root = RepoRoot.Dir;

    static readonly string FullMill =
        Path.Combine(Root, "artifacts", "bin", "Shoddy.Mill", "release", "mill.dll");
    static readonly string HeadlessMill =
        Path.Combine(Root, "artifacts", "bin", "Shoddy.Mill.Headless", "release", "mill.dll");

    static (int Exit, string Out, string Err) Run(string millDll, string args, string cwd)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{millDll}\" {args}")
        {
            WorkingDirectory = cwd,
        };
        return ProcessRun.Capture(psi);
    }

    /// <summary>Both entry points, built fresh so a clean checkout can
    /// run this test — `dotnet build` is a no-op when they already are.</summary>
    static ToolchainEquivalenceTests()
    {
        foreach (string proj in new[] { "src/Shoddy.Mill", "src/Shoddy.Mill.Headless" })
        {
            // A nested `dotnet build` inside `dotnet test` is the chattiest
            // child in the suite - every warning it prints goes to stderr -
            // so this is exactly where reading one pipe at a time deadlocked.
            var psi = new ProcessStartInfo("dotnet", $"build {proj} -c Release --nologo -v q")
            { WorkingDirectory = Root };
            var (exit, text) = ProcessRun.CaptureAll(psi);
            if (exit != 0)
                throw new InvalidOperationException($"dotnet build {proj} failed:\n{text}");
        }
    }

    static string Workspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-equiv", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        return ws;
    }

    [Fact]
    public void SharedVerbsProduceByteIdenticalArtifacts()
    {
        string ws = Workspace();
        File.WriteAllText(Path.Combine(ws, "hello.shoddy"),
            "Def Main()\n    Print(6 * 7)\n");
        File.WriteAllText(Path.Combine(ws, "hycore.shoddy"),
            "Def HyTwice(n As Number) As Number\n    n * 2\n");

        // weave: full first, bytes kept, then headless over the same source.
        Assert.Equal(0, Run(FullMill, "weave hello.shoddy", ws).Exit);
        byte[] fullWoven = File.ReadAllBytes(Path.Combine(ws, "hello.dll"));
        Assert.Equal(0, Run(HeadlessMill, "weave hello.shoddy", ws).Exit);
        Assert.Equal(fullWoven, File.ReadAllBytes(Path.Combine(ws, "hello.dll")));

        // machine: same comparison, same rule.
        string machineDll = Path.Combine(ws, "bin", "Shoddy.Machines.Hycore.dll");
        Assert.Equal(0, Run(FullMill, "machine hycore.shoddy", ws).Exit);
        byte[] fullMachine = File.ReadAllBytes(machineDll);
        File.Delete(machineDll);
        Assert.Equal(0, Run(HeadlessMill, "machine hycore.shoddy", ws).Exit);
        Assert.Equal(fullMachine, File.ReadAllBytes(machineDll));

        // gen: the generated C# itself, stdout to stdout.
        Assert.Equal(Run(FullMill, "gen hello.shoddy", ws).Out,
                     Run(HeadlessMill, "gen hello.shoddy", ws).Out);
    }

    [Fact]
    public void HeadlessRefusesRunAndDapByName()
    {
        string ws = Workspace();
        File.WriteAllText(Path.Combine(ws, "hello.shoddy"), "Def Main()\n    Print(1)\n");

        var run = Run(HeadlessMill, "run hello.shoddy", ws);
        Assert.Equal(1, run.Exit);
        Assert.Contains("headless", run.Err);
        Assert.Contains("full mill", run.Err);
        Assert.DoesNotContain("usage:", run.Err);

        // The default verb is run, so a bare file refuses identically.
        var bare = Run(HeadlessMill, "hello.shoddy", ws);
        Assert.Equal(1, bare.Exit);
        Assert.Contains("headless", bare.Err);

        var dap = Run(HeadlessMill, "dap", ws);
        Assert.Equal(1, dap.Exit);
        Assert.Contains("headless", dap.Err);
        Assert.Contains("full mill", dap.Err);
    }

    [Fact]
    public void VersionNamesTheBuild()
    {
        string ws = Workspace();
        var full = Run(FullMill, "--version", ws);
        var headless = Run(HeadlessMill, "--version", ws);
        Assert.Equal(0, full.Exit);
        Assert.Equal(0, headless.Exit);
        Assert.Contains("(full)", full.Out);
        Assert.Contains("(headless)", headless.Out);
    }
}
