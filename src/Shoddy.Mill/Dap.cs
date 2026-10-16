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
/// The debugger's two processes, as the full mill provides them. The
/// protocol server itself lives in Shoddy.Perch, transport-parameterized
/// (D5); this is the mill-only half: the transports, program compilation
/// with debug instrumentation (Weaver.BuildDebug), lint-before-launch,
/// and the scribbler-window wait.
///
///   mill dap                     the adapter, for an editor: the protocol
///                                over this process's stdin and stdout
///                                (<see cref="PerchAdapter"/>). It opens no
///                                window of its own; it asks the editor for
///                                a terminal window of the operating
///                                system's own and starts the program's
///                                process there.
///   mill run --perch PORT FILE   the program's process, as the adapter
///                                starts it in that window: it connects
///                                back to the adapter at PORT and serves
///                                the perch over that connection, with this
///                                process's console as the program's — its
///                                keyboard, its screen, its error stream,
///                                as `mill run` gives them. The program is
///                                compiled when the launch arrives and runs
///                                on a worker thread while the process's
///                                main thread serves scribbler windows
///                                (ScribblerWindows.Run owns that).
/// </summary>
public static class DapServer
{
    /// <summary>`mill dap`: serve one editor session as the adapter.</summary>
    public static int Adapter()
    {
        var adapter = new PerchAdapter(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                                       MillCommand(), MillEnvironment());
        return adapter.Serve();
    }

    /// <summary>`mill run --perch PORT FILE`: connect to the adapter and
    /// serve the perch over the connection until the session ends.</summary>
    public static int Connect(int port, string file)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine(
                $"mill: nothing is listening for the debugger on port {port} ({e.Message}). " +
                "`mill run --perch` is how the editor's adapter (`mill dap`) starts a debugged " +
                "program; to run a program by hand, use `mill run FILE`.");
            return 2;
        }
        NetworkStream s = client.GetStream();
        return Session(s, s, file).Serve();
    }

    static PerchServer Session(Stream input, Stream output, string file)
    {
        string opened = Path.GetFullPath(file);
        var server = new PerchServer(input, output)
        {
            // A program that returns with a window still open isn't
            // over: `mill run` keeps the picture up until the user
            // closes it, and the session does the same.
            AfterRun = ScribblerRegistry.WaitAllClosed,
        };
        server.Launcher = program =>
        {
            // The launch names the program this window was opened for;
            // the adapter built both from one configuration.
            if (!string.Equals(Path.GetFullPath(program), opened, StringComparison.OrdinalIgnoreCase))
                throw new ShoddyError(0, $"this window was opened for {opened}, and the launch names {program}");
            // Resolve machines exactly as `mill run` does, scoping and
            // all, so the debugger compiles the same program the runner
            // does. The one difference is which weave of each machine:
            // a debug set resolves the instrumented library, so the
            // program steps into every machine it calls, and a
            // breakpoint in a machine's source binds.
            MachineSet machines = MachineSet.Default(debug: true);
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

    /// <summary>How the window starts this same mill: the apphost when
    /// this process is one (`/path/mill`), else the muxer and the
    /// assembly (`dotnet /path/mill.dll`), which is how the extension's
    /// bundled copy runs everywhere but Windows.</summary>
    static IReadOnlyList<string> MillCommand()
    {
        string? exe = Environment.ProcessPath;
        string dll = typeof(DapServer).Assembly.Location;
        if (exe != null && string.Equals(Path.GetFileNameWithoutExtension(exe), "mill", StringComparison.OrdinalIgnoreCase))
            return new[] { exe };
        return new[] { exe ?? "dotnet", dll };
    }

    /// <summary>What the window's mill must share with this one: the
    /// variables that name the library and its sources. The window is
    /// the editor's, opened with the editor's environment, which need
    /// not carry them.</summary>
    static IReadOnlyDictionary<string, string> MillEnvironment()
    {
        var env = new Dictionary<string, string>();
        foreach (string name in new[] { "SHODDYLIB", "SHODDYLIBBIN", "SHODDY_LINT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                env[name] = value;
        return env;
    }
}
