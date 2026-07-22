// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using System.Net;
using System.Net.Sockets;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The TCP/IP builtins on the loopback path. The whole round trip runs in
/// one single-threaded Shoddy program because the readiness ops never
/// block: after a synchronous Connect the listener's Accept finds the
/// pending client at once, so listen / connect / accept / send / recv /
/// echo interleave on one thread with no second party to schedule. The
/// port is handed in as a program argument so the test owns an ephemeral
/// one and never collides. Both tests live in one class so the process-
/// global --allow-net switch is set and cleared without racing itself
/// (xUnit runs a class's tests sequentially).
/// </summary>
public class NetTests
{
    [Fact]
    public void LoopbackEchoRoundTrip()
    {
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let port = Val(First(Args()))\n" +
            "    Let srv = Listen(port)\n" +
            "    Let cli = Connect(\"127.0.0.1\", port)\n" +
            "    Let conn = WaitAccept(srv)\n" +
            "    Send(cli, \"PING\")\n" +
            "    Let got = WaitRecv(conn)\n" +
            "    Send(conn, \"PONG:\" & got)\n" +
            "    Let reply = WaitRecv(cli)\n" +
            "    Print(reply)\n" +
            "    Close(cli)\n" +
            "    Close(conn)\n" +
            "    Close(srv)\n";

        string output = Run(src, allowNet: true, out int exit, port: FreePort());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        Assert.Equal("PONG:PING\n", output);
    }

    [Fact]
    public void RefusedWithoutAllowNet()
    {
        // Same first move, but the mill was not armed: the first TCP word
        // must raise instead of touching the network. Execute catches the
        // ShoddyError inside the woven Run and returns exit 1.
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let srv = Listen(Val(First(Args())))\n" +
            "    Print(\"unreachable\")\n";

        string output = Run(src, allowNet: false, out int exit, port: FreePort());
        Assert.True(exit == 1, $"expected exit 1 (network disabled), got {exit}");
        Assert.Equal("", output);
    }

    // ---- helpers -------------------------------------------------------

    static string Run(string src, bool allowNet, out int exit, int port)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-net", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        // net.shoddy lives beside the program so its Include resolves by source.
        foreach (string f in Directory.GetFiles(
                     Path.Combine(RepoRoot.Dir, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        string prog = Path.Combine(ws, "machines", "prog.shoddy");
        File.WriteAllText(prog, src);

        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(prog, machines.TryResolve);
        var program = new ShoddyProgram();
        machines.SeedInto(program);
        Parser.Parse(lines, program);

        var output = new StringWriter();
        string? prior = Environment.GetEnvironmentVariable("SHODDY_ALLOW_NET");
        Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", allowNet ? "1" : null);
        try
        {
            exit = Weaver.Execute(program, machines.Machines, output,
                                  TextReader.Null, new[] { port.ToString() });
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", prior);
        }
        return output.ToString();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
