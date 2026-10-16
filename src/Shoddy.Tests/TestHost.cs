// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Tests;

/// <summary>
/// What this test host carries beside itself. The test project
/// references the full mill, so the mill's output travels into the
/// host's: mill.dll with its runtime, the standard library in lib/ and
/// lib/debug/, and the machine sources in machines/. A test that needs
/// the toolchain as a process runs that copy through dotnet, and it
/// resolves an Include exactly as a published toolchain does, with
/// nothing built or published ahead of the run.
/// </summary>
static class TestHost
{
    public static readonly string Dir = AppContext.BaseDirectory;

    /// <summary>The full mill, as `dotnet MillDll ...` runs it.</summary>
    public static readonly string MillDll = Path.Combine(Dir, "mill.dll");

    /// <summary>The standard library's compiled form, release and
    /// instrumented.</summary>
    public static string Library(bool debug = false) =>
        Path.Combine(Dir, "lib", debug ? Path.Combine("debug", "Shoddy.Machines.dll") : "Shoddy.Machines.dll");
}
