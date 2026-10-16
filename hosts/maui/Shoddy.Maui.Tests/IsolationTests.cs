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
/// Only-what-it-needs (B1.6): the app's linked Shoddy set is the woven
/// cores of its declared mills and the two machine libraries they were
/// woven against, nothing else — enumerated from the build output. The
/// library is one assembly, so which machines a mill folds is a fact
/// about its manifest (EverySeedTests reads it), and what this proves is
/// that nothing woven or linked sits outside the declaration.
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

        // The declaration: the Mode N core, and the libraries the
        // project references. The Mode T games are woven whole under
        // their own names and are not Shoddy.Machines.* assemblies.
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shoddy.Machines.Halifax-core.dll",
            "Shoddy.Machines.dll",
            "Shoddy.Machines.Seeds.dll",
        };

        var linked = Directory.GetFiles(appOut, "Shoddy.Machines*.dll")
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
