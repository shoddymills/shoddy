// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Tests;

/// <summary>
/// Locates the repository root by walking up from the test assembly's
/// directory until the tree's landmarks (build.sh beside machines/)
/// appear, so the golden tests work no matter how deeply the build
/// output is nested — per-project bin/obj, the centralized artifacts/
/// layout (which has one fewer level than the old bin/Debug/netX.Y),
/// or whatever comes next.
/// </summary>
static class RepoRoot
{
    public static readonly string Dir = Find();

    static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "build.sh")) &&
                Directory.Exists(Path.Combine(d.FullName, "machines")))
                return d.FullName;
        throw new DirectoryNotFoundException(
            $"Shoddy repo root not found above {AppContext.BaseDirectory}");
    }
}
