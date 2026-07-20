// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

namespace Shoddy.Tests;

/// <summary>
/// Locates the repository root by walking up from the test assembly's
/// directory until the tree's landmarks (build.cmd beside machines/)
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
            if (File.Exists(Path.Combine(d.FullName, "build.cmd")) &&
                Directory.Exists(Path.Combine(d.FullName, "machines")))
                return d.FullName;
        throw new DirectoryNotFoundException(
            $"Shoddy repo root not found above {AppContext.BaseDirectory}");
    }
}
