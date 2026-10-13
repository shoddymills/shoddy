// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Perch;
using Shoddy.Runtime;

namespace Shoddy.Mill;

/// <summary>
/// `mill dap`, since the perch split (D5): the protocol server lives in
/// Shoddy.Perch, transport-parameterized; this wrapper is the mill-only
/// half — the transport, program compilation with debug instrumentation
/// (Weaver.LoadDebug), lint-before-launch, and the scribbler-window wait.
/// The compiled program runs on a worker thread while the process's main
/// thread serves scribbler windows (ScribblerWindows.Run owns that
/// arrangement).
///
/// Two transports:
///
///   mill dap                  the protocol over this process's stdin and
///                             stdout. The program's output reaches the
///                             editor as output events, and it has no
///                             keyboard: the editor's "internalConsole".
///   mill dap --listen PORT    the protocol over one loopback TCP
///                             connection, so this process's console
///                             belongs to the program: its keyboard, its
///                             screen, its error stream, as `mill run`
///                             gives them. The editor starts this in an
///                             integrated terminal ("integratedTerminal",
///                             the default).
/// </summary>
public static class DapServer
{
    public static int Serve() => Serve(Console.OpenStandardInput(), Console.OpenStandardOutput(), consoleIo: false);

    /// <summary>Accept a client on the loopback at <paramref name="port"/>
    /// and serve it. One session per process: the debugged program owns
    /// the console, and the session ends with it.
    ///
    /// <para>A connection that closes without sending a request is not a
    /// session: it is somebody checking that the port is up (the editor
    /// does exactly that while it waits for this process to start), and
    /// the next connection is accepted instead.</para></summary>
    public static int Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (true)
            {
                using TcpClient client = listener.AcceptTcpClient();
                NetworkStream s = client.GetStream();
                PerchServer server = Session(s, s, consoleIo: true);
                int rc = server.Serve();
                if (server.Spoke) return rc;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    static int Serve(Stream input, Stream output, bool consoleIo) =>
        Session(input, output, consoleIo).Serve();

    static PerchServer Session(Stream input, Stream output, bool consoleIo)
    {
        var server = new PerchServer(input, output)
        {
            ConsoleIo = consoleIo,
            // A program that returns with a window still open isn't
            // over: `mill run` keeps the picture up until the user
            // closes it, and the session does the same.
            AfterRun = ScribblerRegistry.WaitAllClosed,
        };
        server.Launcher = program =>
        {
            // Resolve machines exactly as `mill run` does, scoping and
            // all, so the debugger compiles the same program the runner
            // does. The one difference is which weave of each machine:
            // a debug set resolves (and builds when missing or stale) the
            // instrumented one, so the program steps into every machine
            // it calls, and a breakpoint in a machine's source binds.
            var machines = new MachineSet(debug: true);
            List<Line> lines = Lexer.ReadProgram(program, (p, q) => MachineResolve.Resolve(machines, p, q));
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
            // The build carries the evaluator too: the Debug Console,
            // watches, hovers, breakpoint conditions, logpoint messages
            // and set-variable all compile their expression against it.
            DebugBuild build = Weaver.BuildDebug(parsed, machines.Machines);
            server.Evaluator = build.Evaluate;
            return build.Run;
        };
        return server;
    }
}
