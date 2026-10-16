// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// How an Include becomes a compiled machine. Every entry point that
/// reads a program — the shared verbs, `mill run`, and the debug
/// adapter — goes through here, so the debugger cannot end up compiling
/// a different program than the runner.
///
/// A machine is a runtime. It is compiled, and it is used compiled: its
/// source shipping alongside is for reading, not for splicing into
/// whoever includes it. And it is compiled by its library's build, never
/// here: resolving a program must not have side effects in a source
/// tree, and whether a program is legal must not depend on what happened
/// to be built. A machine that is in no compiled library is an error that
/// names the build to run.
/// </summary>
public static class MachineResolve
{
    /// <summary>Resolve one Include. Returns true when a compiled machine
    /// satisfies it, false to let the Lexer splice the source.
    ///
    /// A machine library file is always a machine. Anything else — a
    /// program's own sibling files — splices, which is what lets a mill
    /// spread itself across a dozen files.
    ///
    /// A debug set (the debugger's) resolves the instrumented weave
    /// instead, so every machine a debugged program reaches can be
    /// stepped into.</summary>
    public static bool Resolve(MachineSet machines, string sbPath, string? qual)
    {
        // Only a file in a machine library is a machine. A program's own
        // siblings splice — and keep splicing even when a woven artifact
        // sits somewhere: a mill whose core has been machine-woven must
        // not have its shell and tests quietly rescoped by the build's
        // leftovers.
        if (!Lexer.IsMachineLibrary(sbPath)) return false;

        if (machines.TryResolve(sbPath, qual)) return true;

        string kind = machines.Debug ? "instrumented machine" : "machine";
        throw new ShoddyError(0,
            $"{kind} {Path.GetFileName(sbPath)} is in no compiled library (looked in: {machines.Describe()}). " +
            "Build the library it belongs to (dotnet build on its project), or name a compiled " +
            "library with --lib, or point SHODDYLIBBIN at a folder of them");
    }
}
