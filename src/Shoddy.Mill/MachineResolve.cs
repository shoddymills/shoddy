// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Mill;

/// <summary>
/// How an Include becomes a compiled machine. Both entry points that read
/// a program — `mill run`/`weave`, and the debug adapter — go through
/// here, so the debugger cannot end up compiling a different program than
/// the runner.
///
/// A machine is a runtime. It is compiled, and it is used compiled: its
/// source shipping alongside is for reading, not for splicing into
/// whoever includes it. Everything below follows from that.
/// </summary>
static class MachineResolve
{
    /// <summary>Resolve one Include. Returns true when a machine DLL
    /// satisfies it, false to let the Lexer splice the source.
    ///
    /// A machine library file is always resolved: built if it has no DLL,
    /// rebuilt if the DLL is behind its source. Anything else — a
    /// program's own sibling files — splices, which is what lets a mill
    /// spread itself across a dozen files.</summary>
    public static bool Resolve(MachineSet machines, string sbPath, string? qual)
    {
        string dll = MachineSet.DllPathFor(sbPath);

        if (!File.Exists(dll))
        {
            // Splicing a machine would put its dependencies in the
            // includer's flat table and quietly undo the scoping — so
            // whether a program is legal would depend on what happened to
            // be built. Build it instead; the mill already knows how.
            if (!Lexer.IsMachineLibrary(sbPath)) return false;
            Console.Error.WriteLine(
                $"mill: machine {Path.GetFileName(sbPath)} is not built — building");
            Build(sbPath);
        }
        else if (IsStale(sbPath, dll))
        {
            // The stale-DLL trap cost two debugging rounds in one week.
            Console.Error.WriteLine(
                $"mill: machine {Path.GetFileName(sbPath)} is newer than its DLL — rebuilding");
            Build(sbPath);
        }

        return machines.TryResolve(sbPath, qual);
    }

    /// <summary>Weave one machine to its DLL. Build failures fall through
    /// to the normal error path — the message names the machine.</summary>
    static void Build(string sbPath)
    {
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sbPath, (p, q) => Resolve(machines, p, q));
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sbPath, machines.Machines);
    }

    /// <summary>A DLL older than its source, or than any DLL it was built
    /// against: its calls bind to a surface that has since moved.</summary>
    static bool IsStale(string sbPath, string dll)
    {
        DateTime built = File.GetLastWriteTimeUtc(dll);
        if (File.GetLastWriteTimeUtc(sbPath) > built) return true;
        string dir = Path.GetDirectoryName(Path.GetFullPath(sbPath))!;
        foreach (string line in File.ReadLines(sbPath))
        {
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(
                    line, "^Include \"([A-Za-z0-9_.-]+\\.shoddy)\"");
            if (!m.Success) continue;
            string depDll = MachineSet.DllPathFor(Path.Combine(dir, m.Groups[1].Value));
            if (File.Exists(depDll) && File.GetLastWriteTimeUtc(depDll) > built) return true;
        }
        return false;
    }
}
