// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Perch;
using Shoddy.Runtime;

namespace Shoddy.Mill;

/// <summary>
/// `mill dap`, since the perch split (D5): the protocol server lives in
/// Shoddy.Perch, transport-parameterized; this wrapper is the mill-only
/// half — the stdio transport, program compilation with debug
/// instrumentation (Weaver.LoadDebug), lint-before-launch, and the
/// scribbler-window wait. VS Code launches `mill dap`, sends a program
/// path, and the compiled program runs on a worker thread while the
/// process's main thread serves scribbler windows (ScribblerWindows.Run
/// owns that arrangement).
/// </summary>
public static class DapServer
{
    public static int Serve()
    {
        var machineSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var server = new PerchServer(Console.OpenStandardInput(), Console.OpenStandardOutput())
        {
            Precompiled = path => machineSources.Contains(path),
            // A program that returns with a window still open isn't
            // over: `mill run` keeps the picture up until the user
            // closes it, and the session does the same.
            AfterRun = ScribblerRegistry.WaitAllClosed,
        };
        server.Launcher = program =>
        {
            // Resolve machines exactly as `mill run` does. A machine is
            // precompiled — that its source ships alongside is a
            // convenience, not an invitation to splice it — so the
            // debugger compiles the same program the runner does,
            // scoping and all. Before this it spliced everything, and
            // would happily launch a program the runner refused.
            var machines = new MachineSet();
            List<Line> lines = Lexer.ReadProgram(program, (p, q) =>
            {
                if (!MachineResolve.Resolve(machines, p, q)) return false;
                machineSources.Add(Path.GetFullPath(p));
                return true;
            });
            var prog = new ShoddyProgram();
            machines.SeedInto(prog);
            ShoddyProgram parsed = Parser.Parse(lines, prog);
            // And lint it, for the unknown words the weave would
            // otherwise turn into a runtime failure at whatever line
            // happened to reach them — a debugger that launches a
            // program the runner rejects is the wrong way round.
            Lint.Result lint = Lint.Run(parsed, lines);
            if (lint.UnknownWords.Count > 0)
            {
                (string msg, string? f, int ln) = lint.UnknownWords[0];
                throw new ShoddyError(ln, f, msg);
            }
            return Weaver.LoadDebug(parsed, machines.Machines);
        };
        return server.Serve();
    }
}
