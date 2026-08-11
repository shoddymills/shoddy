// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text.Json;
using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// Two B8 boxes, both asserted rather than inspected:
///
/// Only-what-it-needs (B1.6): the app's linked machine set equals the
/// union of its declared mills' transitive closures — halifax's whole
/// fold, nothing else — enumerated from the build output. Reckoner
/// sits at the ceiling and is compliant while linking almost
/// everything; a machine OUTSIDE the closure is the violation.
///
/// The exclusion (B4.11): no listen, accept or blocking-wait word is
/// reachable from the app's dictionary, asserted against the seeded
/// word list itself — so a future seed that introduces one fails here
/// rather than shipping quietly.
/// </summary>
public class IsolationTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "mills")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void TheLinkedMachineSetEqualsTheDeclaredClosure()
    {
        string root = RepoRoot();
        string appOut = Path.Combine(root, "artifacts", "bin", "ShoddyReckoner",
            "debug_net10.0-windows10.0.19041.0");
        Assert.True(Directory.Exists(appOut),
            "build shoddy-reckoner before running the tests — the enumeration reads its output");

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "mills", "halifax", "mill.manifest")));
        var declared = manifest.RootElement.GetProperty("machines")
            .EnumerateArray().Select(m => m.GetString()!)
            .Select(m => $"Shoddy.Machines.{char.ToUpperInvariant(m[0])}{m[1..]}.dll")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        declared.Add("Shoddy.Machines.Halifax-core.dll");

        var linked = Directory.GetFiles(appOut, "Shoddy.Machines.*.dll")
            .Select(Path.GetFileName).Select(f => f!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(linked.SetEquals(declared),
            "outside the closure: " + string.Join(", ", linked.Except(declared)) +
            " | missing from the app: " + string.Join(", ", declared.Except(linked)));

        // And nothing from above the floor rides along (B2.4, B8):
        // no compiler, no mill, no desktop windowing or audio stack.
        string[] all = Directory.GetFiles(appOut, "*.dll")
            .Select(Path.GetFileName).Select(f => f!).ToArray();
        string[] forbidden = { "Shoddy.Compiler", "Shoddy.Devil", "Shoddy.Mill",
                               "Silk.NET", "OpenAL", "glfw", "Microsoft.CodeAnalysis" };
        foreach (string dll in all)
            foreach (string bad in forbidden)
                Assert.False(dll.Contains(bad, StringComparison.OrdinalIgnoreCase),
                    $"the app links past its closure: {dll}");
    }

    [Fact]
    public async Task NoListenAcceptOrBlockingWaitWordIsReachable()
    {
        string root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
        _ = session.Opening();

        HalifaxTurn? words = await session.SubmitAsync("WORDS");
        Assert.NotNull(words);
        string dictionary = string.Join("\n", words!.Shown).ToUpperInvariant();

        string[] excluded = { "LISTEN", "LISTENON", "ACCEPT", "WAITACCEPT",
                              "WAITRECV", "WAITRECVFOR", "WAITRECVUNTIL" };
        foreach (string word in excluded)
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(dictionary, $@"\b{word}\b"),
                $"the exclusion is breached: {word} is in the calculator's dictionary (B4.11)");
    }
}
