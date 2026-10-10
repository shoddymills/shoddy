// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
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
/// Recursion has no practical limit. A non-tail recursion two million
/// deep returns under every launcher: `mill run`, a woven program's own
/// Main, the hosting surface's RunWovenAsync, and the perch's launch.
/// Each gives the program a thread of Engine.ProgramStackBytes; before
/// this they gave it a 1 MB thread or a thread-pool thread, and about
/// twenty thousand frames were a process kill with no line number.
///
/// Every test goes through a launcher the way a user reaches it, never
/// through Weaver.Execute on the test's own thread: the test thread's
/// stack is exactly what these must not rely on.
///
/// Golden collection: the published mill and Engine.PendingSink are
/// shared state.
/// </summary>
[Collection("golden")]
public class DepthTests
{
    static readonly string Root = RepoRoot.Dir;
    const int Depth = 2_000_000;

    static readonly string DeepSource = string.Join('\n',
        "Def Deep(n As Number) As Number",
        "    If n = 0 Then",
        "        0",
        "    Else",
        "        1 + Deep(n - 1)",
        "",
        "Def Main()",
        $"    Print(Deep({Depth}))",
        "");

    static string Expected => Depth.ToString();

    /// <summary>The published mill, as ProofTests reaches it: built once
    /// if absent, on a cold checkout under plain `dotnet test`.</summary>
    static DepthTests()
    {
        if (!File.Exists(MillPath))
            ProcessRun.CaptureAll(new ProcessStartInfo("dotnet", "publish src/Shoddy.Mill -c Release -o bin")
            { WorkingDirectory = Root });
    }

    static string MillPath => Path.Combine(Root, "bin",
        OperatingSystem.IsWindows() ? "mill.exe" : "mill");

    static string Workspace(string name)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-depth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, name + ".shoddy"), DeepSource);
        return ws;
    }

    static (int Exit, string Out, string Err) Run(string exe, string args, string cwd) =>
        ProcessRun.Capture(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd });

    [Fact]
    public void MillRunReturnsFromTwoMillionFrames()
    {
        string ws = Workspace("deep");
        var (exit, o, e) = Run(MillPath, "run deep.shoddy", ws);
        Assert.True(exit == 0, o + e);
        Assert.Equal(Expected, o.Trim());
    }

    [Fact]
    public void AWovenProgramsOwnMainReturnsFromTwoMillionFrames()
    {
        string ws = Workspace("deep");
        var weave = Run(MillPath, "weave deep.shoddy", ws);
        Assert.True(weave.Exit == 0, weave.Out + weave.Err);
        var (exit, o, e) = Run("dotnet", "deep.dll", ws);
        Assert.True(exit == 0, o + e);
        Assert.Equal(Expected, o.Trim());
    }

    [Fact]
    public async Task RunWovenAsyncReturnsFromTwoMillionFrames()
    {
        // Its own assembly name: a woven dll is loaded by name into the
        // test process, and two tests must not load two files as one.
        string ws = Workspace("deephost");
        string sb = Path.Combine(ws, "deephost.shoddy");
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);
        string dll = Path.Combine(ws, "deephost.dll");
        Weaver.Weave(parsed, dll, machines.Machines);
        Assembly asm = Assembly.LoadFrom(dll);

        var output = new StringWriter();
        int rc = await ShoddyHost.RunWovenAsync(asm, output, TextReader.Null, Array.Empty<string>());
        Assert.Equal(0, rc);
        Assert.Equal(Expected, output.ToString().Trim());
    }

    [Fact]
    public async Task ThePerchLaunchReturnsFromTwoMillionFrames()
    {
        // The debug weave carries a frame per Def call for the stack
        // view, a try/finally round each, and a line hook per statement:
        // the heaviest frame any launcher builds, so the one most worth
        // proving at this depth.
        string ws = Workspace("deep");
        string sb = Path.Combine(ws, "deep.shoddy");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<int> serverRun = Task.Run(() =>
        {
            using TcpClient conn = listener.AcceptTcpClient();
            NetworkStream s = conn.GetStream();
            var server = new PerchServer(s, s)
            {
                Launcher = program => Weaver.LoadDebug(Parser.Parse(Lexer.ReadProgram(program))),
            };
            return server.Serve();
        });

        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        using var client = new Dap(tcp.GetStream());
        var said = new StringBuilder();
        try
        {
            client.Request("initialize");
            client.WaitFor(m => Dap.IsEvent(m, "initialized"), said).Dispose();
            client.Request("launch", new { program = sb });
            using (JsonDocument launched = client.WaitFor(m => Dap.IsResponse(m, "launch"), said))
                Assert.True(launched.RootElement.GetProperty("success").GetBoolean(),
                    launched.RootElement.ToString());
            client.Request("configurationDone");
            using (JsonDocument exited = client.WaitFor(m => Dap.IsEvent(m, "exited"), said))
                Assert.Equal(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
            client.Request("disconnect");
        }
        finally
        {
            listener.Stop();
            Engine.PendingSink = null;
        }
        Assert.Contains(Expected, said.ToString());
        await serverRun.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The least DAP client that can launch a program and read
    /// what it said: framed requests out, framed messages in, output
    /// events gathered on the way past.</summary>
    sealed class Dap : IDisposable
    {
        readonly NetworkStream stream;
        int seq;

        public Dap(NetworkStream stream) => this.stream = stream;

        public static bool IsEvent(JsonElement m, string name) =>
            m.GetProperty("type").GetString() == "event" && m.GetProperty("event").GetString() == name;

        public static bool IsResponse(JsonElement m, string command) =>
            m.GetProperty("type").GetString() == "response" && m.GetProperty("command").GetString() == command;

        public void Request(string command, object? args = null)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new { seq = ++seq, type = "request", command, arguments = args });
            byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            stream.Write(head);
            stream.Write(body);
            stream.Flush();
        }

        /// <summary>Read framed messages until one satisfies the predicate.
        /// Output events are appended to <paramref name="said"/> as they
        /// pass. Bounded so a hang fails rather than wedges.</summary>
        public JsonDocument WaitFor(Func<JsonElement, bool> want, StringBuilder said)
        {
            for (int i = 0; i < 200; i++)
            {
                JsonDocument msg = Read();
                if (IsEvent(msg.RootElement, "output"))
                    said.Append(msg.RootElement.GetProperty("body").GetProperty("output").GetString());
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
}
