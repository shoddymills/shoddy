// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Hosting;
using Shoddy.Perch;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The instrumented-weave proof, level one (D13): a Perch session
/// attached over a real transport — loopback TCP, the same shape a
/// host's debug build serves — to a --debug machine-woven core, hitting
/// a breakpoint on a source line, reading the stack, and continuing.
/// Level two is F5 in a scaffolded project; this is the half a unit
/// test can hold.
///
/// Golden collection: Engine.PendingSink is process-global.
/// </summary>
[Collection("golden")]
public class PerchTests
{
    const string CoreSource = """
        Def PTwice(n As Number) As Number
            n * 2

        Def PSum(a As Number, b As Number) As Number
            PTwice(a) + PTwice(b)
        """;

    sealed class DapClient : IDisposable
    {
        readonly NetworkStream stream;
        public DapClient(NetworkStream stream) => this.stream = stream;

        int seq;

        public void Request(string command, object? args = null) =>
            Write(new { seq = ++seq, type = "request", command, arguments = args });

        void Write(object payload)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
            byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            stream.Write(head);
            stream.Write(body);
            stream.Flush();
        }

        /// <summary>Read framed messages until one satisfies the
        /// predicate; everything else (responses, output events) is
        /// let pass. Bounded so a hang fails rather than wedges.</summary>
        public JsonDocument WaitFor(Func<JsonElement, bool> want)
        {
            for (int i = 0; i < 50; i++)
            {
                JsonDocument msg = Read();
                if (want(msg.RootElement)) return msg;
                msg.Dispose();
            }
            throw new TimeoutException("expected message never arrived");
        }

        JsonDocument Read()
        {
            int len = -1;
            var line = new StringBuilder();
            while (true)
            {
                int c = stream.ReadByte();
                if (c < 0) throw new IOException("transport closed");
                if (c == '\n')
                {
                    string h = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (h.Length == 0) break;
                    if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        len = int.Parse(h["Content-Length:".Length..].Trim());
                }
                else line.Append((char)c);
            }
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = stream.Read(buf, got, len - got);
                if (n <= 0) throw new IOException("transport closed");
                got += n;
            }
            return JsonDocument.Parse(buf);
        }

        public void Dispose() => stream.Dispose();
    }

    [Fact]
    public void BreakpointHitsInAMachineWovenCoreOverAttach()
    {
        // The instrumented machine weave, exactly as a debug
        // configuration builds it.
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-perch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        string sb = Path.Combine(ws, "perchcore.shoddy");
        File.WriteAllText(sb, CoreSource);
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        string dll = Weaver.WeaveMachine(Parser.Parse(lines, prog), sb, machines.Machines, debug: true);
        Assembly asm = Assembly.LoadFrom(dll);

        // The attach transport: loopback TCP, the host-side shape.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<int> serverRun = Task.Run(() =>
        {
            using TcpClient conn = listener.AcceptTcpClient();
            NetworkStream s = conn.GetStream();
            return new PerchServer(s, s).Serve();
        });

        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        using var client = new DapClient(tcp.GetStream());

        try
        {
            client.Request("initialize");
            client.WaitFor(m => m.GetProperty("type").GetString() == "event" &&
                                m.GetProperty("event").GetString() == "initialized").Dispose();
            client.Request("attach");
            client.WaitFor(m => m.GetProperty("type").GetString() == "response" &&
                                m.GetProperty("command").GetString() == "attach").Dispose();
            client.Request("setBreakpoints", new
            {
                source = new { path = sb },
                breakpoints = new[] { new { line = 2 } },
            });
            using (JsonDocument bp = client.WaitFor(m =>
                m.GetProperty("type").GetString() == "response" &&
                m.GetProperty("command").GetString() == "setBreakpoints"))
            {
                JsonElement first = bp.RootElement.GetProperty("body")
                    .GetProperty("breakpoints").EnumerateArray().First();
                Assert.True(first.GetProperty("verified").GetBoolean());
            }
            client.Request("configurationDone");

            // The host's side: load AFTER attach, so the engine picks the
            // pending sink up at construction, and call a word that runs
            // through the breakpointed line.
            Task<double> call = Task.Run(() =>
            {
                ShoddyHost host = ShoddyHost.Load(asm);
                return host.Word("PSum").Call(ShoddyValue.Num(3), ShoddyValue.Num(4)).AsNum();
            });

            using (JsonDocument stopped = client.WaitFor(m =>
                m.GetProperty("type").GetString() == "event" &&
                m.GetProperty("event").GetString() == "stopped"))
            {
                Assert.Equal("breakpoint",
                    stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
            }

            client.Request("stackTrace", new { threadId = 1 });
            using (JsonDocument st = client.WaitFor(m =>
                m.GetProperty("type").GetString() == "response" &&
                m.GetProperty("command").GetString() == "stackTrace"))
            {
                JsonElement top = st.RootElement.GetProperty("body")
                    .GetProperty("stackFrames").EnumerateArray().First();
                Assert.Equal(2, top.GetProperty("line").GetInt32());
                Assert.Equal(Path.GetFullPath(sb),
                    top.GetProperty("source").GetProperty("path").GetString());
                Assert.Equal("PTWICE", top.GetProperty("name").GetString());
            }

            // Continue: the word hits the breakpoint once more (PSum
            // calls PTwice twice), so resume with the breakpoints
            // cleared for a clean run to the answer.
            client.Request("setBreakpoints", new
            {
                source = new { path = sb },
                breakpoints = Array.Empty<object>(),
            });
            client.WaitFor(m => m.GetProperty("type").GetString() == "response" &&
                                m.GetProperty("command").GetString() == "setBreakpoints").Dispose();
            client.Request("continue", new { threadId = 1 });

            Assert.True(call.Wait(TimeSpan.FromSeconds(20)), "the call never completed");
            Assert.Equal(14, call.Result);

            client.Request("disconnect");
        }
        finally
        {
            listener.Stop();
            Engine.PendingSink = null;
        }
        Assert.True(serverRun.Wait(TimeSpan.FromSeconds(10)), "the server never returned");
    }
}
