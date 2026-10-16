// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Perch;

namespace Shoddy.Tests;

/// <summary>
/// The adapter as the editor drives it: F5 with no editor. The test is
/// the editor, speaking the protocol to the adapter; it also plays the
/// terminal window the adapter asks the editor for, by running what the
/// request names. In process, the adapter is a <see cref="PerchAdapter"/>
/// over a loopback connection standing in for its stdin and stdout, and
/// the window's mill is a perch in this process connecting back. Out of
/// process, it is the real thing: `mill dap` from the mill this test
/// host carries, and the window's command run as a process.
///
/// Golden collection: the perch played in process sets the engine's
/// pending sink, which is shared state.
/// </summary>
[Collection("golden")]
public class PerchAdapterTests
{
    static readonly string Root = RepoRoot.Dir;

    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-adapter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Program(string dir, string name, string source)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    // ---- in process -------------------------------------------------------

    [Fact]
    public async Task ALaunchOpensAWindowRunningTheMillOnTheProgramAndRelaysTheSession()
    {
        string dir = Folder();
        string prog = Program(dir, "adapterquiet.shoddy", "Def Main()\n    Let x = 1\n");
        var (_, editor, serving) = InProcess(new Dictionary<string, string> { ["SHODDYLIB"] = "/lib" });
        Task<int>? window = null;
        using (editor)
        {
            editor.Request("initialize", new { adapterID = "shoddy", supportsRunInTerminalRequest = true });
            using (JsonDocument init = editor.WaitForResponse("initialize"))
                Assert.True(init.RootElement.GetProperty("body").GetProperty("supportsConfigurationDoneRequest").GetBoolean());

            editor.Request("launch", new { program = prog, cwd = dir });
            using (JsonDocument ask = editor.WaitForRequest("runInTerminal"))
            {
                JsonElement a = ask.RootElement.GetProperty("arguments");
                Assert.Equal("external", a.GetProperty("kind").GetString());
                Assert.Equal("shoddy debug: adapterquiet.shoddy", a.GetProperty("title").GetString());
                Assert.Equal(dir, a.GetProperty("cwd").GetString());
                Assert.Equal("/lib", a.GetProperty("env").GetProperty("SHODDYLIB").GetString());
                string[] args = a.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray();
                Assert.Equal(new[] { "mill", "run", "--perch" }, args[..3]);
                Assert.Equal(prog, args[4]);
                // The window: the program's process, played here, connecting
                // back as `mill run --perch` does.
                window = PerchInThisProcess(int.Parse(args[3]));
                editor.Answer(ask.RootElement, success: true, new { processId = Environment.ProcessId });
            }
            editor.WaitForEvent("initialized").Dispose();
            using (JsonDocument launched = editor.WaitForResponse("launch"))
                Assert.True(launched.RootElement.GetProperty("success").GetBoolean(), launched.RootElement.ToString());
            editor.Request("configurationDone");
            editor.WaitForResponse("configurationDone").Dispose();
            using (JsonDocument exited = editor.WaitForEvent("exited"))
                Assert.Equal(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
            editor.WaitForEvent("terminated").Dispose();
            editor.Request("disconnect");
            editor.WaitForResponse("disconnect").Dispose();
        }
        Assert.Equal(0, await Done(serving));
        Assert.Equal(0, await Done(window!));
    }

    [Fact]
    public async Task WithoutDebuggingTheWindowRunsTheProgramUnderMillRunAndTheSessionEnds()
    {
        string dir = Folder();
        string prog = Program(dir, "plain.shoddy", "Def Main()\n    Print(1)\n");
        var (_, editor, serving) = InProcess();
        using (editor)
        {
            editor.Request("initialize", new { supportsRunInTerminalRequest = true });
            editor.WaitForResponse("initialize").Dispose();
            editor.Request("launch", new
            {
                program = prog, cwd = dir, noDebug = true, allowNet = true, args = new[] { "words.txt", "42" },
            });
            using (JsonDocument ask = editor.WaitForRequest("runInTerminal"))
            {
                JsonElement a = ask.RootElement.GetProperty("arguments");
                Assert.Equal("external", a.GetProperty("kind").GetString());
                Assert.Equal("shoddy run: plain.shoddy", a.GetProperty("title").GetString());
                Assert.Equal(new[] { "mill", "run", "--allow-net", prog, "words.txt", "42" },
                    a.GetProperty("args").EnumerateArray().Select(x => x.GetString()).ToArray());
                editor.Answer(ask.RootElement, success: true, new { processId = Environment.ProcessId });
            }
            using (JsonDocument launched = editor.WaitForResponse("launch"))
                Assert.True(launched.RootElement.GetProperty("success").GetBoolean());
            editor.WaitForEvent("terminated").Dispose();
            editor.Request("disconnect");
            editor.WaitForResponse("disconnect").Dispose();
        }
        Assert.Equal(0, await Done(serving));
    }

    [Fact]
    public async Task AnEditorThatCannotOpenATerminalWindowIsRefused()
    {
        string dir = Folder();
        string prog = Program(dir, "plain.shoddy", "Def Main()\n    Print(1)\n");
        var (_, editor, serving) = InProcess();
        using (editor)
        {
            editor.Request("initialize", new { adapterID = "shoddy" });     // no terminal on offer
            editor.WaitForResponse("initialize").Dispose();
            editor.Request("launch", new { program = prog });
            using (JsonDocument launched = editor.WaitForResponse("launch"))
            {
                Assert.False(launched.RootElement.GetProperty("success").GetBoolean());
                Assert.Contains("terminal window", launched.RootElement.GetProperty("message").GetString());
            }
            editor.WaitForEvent("terminated").Dispose();
            editor.Request("disconnect");
            editor.WaitForResponse("disconnect").Dispose();
        }
        Assert.Equal(0, await Done(serving));
    }

    [Fact]
    public async Task AWindowWhoseMillNeverConnectsFailsTheLaunch()
    {
        string dir = Folder();
        string prog = Program(dir, "plain.shoddy", "Def Main()\n    Print(1)\n");
        var (adapter, editor, serving) = InProcess();
        adapter.StartTimeout = TimeSpan.FromSeconds(1);
        using (editor)
        {
            editor.Request("initialize", new { supportsRunInTerminalRequest = true });
            editor.WaitForResponse("initialize").Dispose();
            editor.Request("launch", new { program = prog });
            using (JsonDocument ask = editor.WaitForRequest("runInTerminal"))
                editor.Answer(ask.RootElement, success: true);       // a window, and nothing in it
            using (JsonDocument launched = editor.WaitForResponse("launch"))
            {
                Assert.False(launched.RootElement.GetProperty("success").GetBoolean());
                Assert.Contains("did not start its debugger within 1 seconds",
                    launched.RootElement.GetProperty("message").GetString());
            }
            editor.WaitForEvent("terminated").Dispose();
            editor.Request("disconnect");
            editor.WaitForResponse("disconnect").Dispose();
        }
        Assert.Equal(0, await Done(serving));
    }

    [Fact]
    public async Task AWindowTheEditorCannotOpenFailsTheLaunchWithItsReason()
    {
        string dir = Folder();
        string prog = Program(dir, "plain.shoddy", "Def Main()\n    Print(1)\n");
        var (_, editor, serving) = InProcess();
        using (editor)
        {
            editor.Request("initialize", new { supportsRunInTerminalRequest = true });
            editor.WaitForResponse("initialize").Dispose();
            editor.Request("launch", new { program = prog });
            using (JsonDocument ask = editor.WaitForRequest("runInTerminal"))
                editor.Answer(ask.RootElement, success: false, message: "no terminal is configured");
            using (JsonDocument launched = editor.WaitForResponse("launch"))
            {
                Assert.False(launched.RootElement.GetProperty("success").GetBoolean());
                Assert.Contains("no terminal is configured", launched.RootElement.GetProperty("message").GetString());
            }
            editor.WaitForEvent("terminated").Dispose();
            editor.Request("disconnect");
            editor.WaitForResponse("disconnect").Dispose();
        }
        Assert.Equal(0, await Done(serving));
    }

    // ---- out of process: the real mill, both ends ---------------------------

    /// <summary>F5 on a program that reads the keyboard and stops at a
    /// breakpoint, with the real adapter and the real window process.</summary>
    [Fact]
    public async Task TheEditorsAdapterDebugsAProgramInAWindowOfItsOwn()
    {
        string dir = Folder();
        string prog = Program(dir, "greet.shoddy", """
            Def Main()
                Let name = Input("name? ")
                Print("HELLO " & name)
            """);

        var psi = new ProcessStartInfo("dotnet", $"\"{TestHost.MillDll}\" dap")
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SHODDYLIB"] = Path.Combine(Root, "machines");
        psi.Environment.Remove("SHODDYLIBBIN");
        using Process adapter = Process.Start(psi)!;
        Task<string> adapterErrors = adapter.StandardError.ReadToEndAsync();
        var editor = new Editor(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream);
        Process? window = null;
        try
        {
            editor.Request("initialize", new { adapterID = "shoddy", supportsRunInTerminalRequest = true });
            editor.WaitForResponse("initialize").Dispose();
            editor.Request("launch", new { program = prog, cwd = dir });
            using (JsonDocument ask = editor.WaitForRequest("runInTerminal"))
            {
                JsonElement a = ask.RootElement.GetProperty("arguments");
                Assert.Equal("external", a.GetProperty("kind").GetString());
                Assert.Equal(dir, a.GetProperty("cwd").GetString());
                string[] args = a.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray();
                Assert.Contains("--perch", args);
                Assert.Equal(prog, args[^1]);
                // The window, played here: the command the editor would run
                // in it, in the folder and with the environment it asked
                // for, its stdin the keyboard.
                var run = new ProcessStartInfo(args[0])
                {
                    WorkingDirectory = a.GetProperty("cwd").GetString(),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string x in args[1..]) run.ArgumentList.Add(x);
                if (a.TryGetProperty("env", out JsonElement env) && env.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty e in env.EnumerateObject())
                        run.Environment[e.Name] = e.Value.GetString();
                Assert.Equal(Path.Combine(Root, "machines"), run.Environment["SHODDYLIB"]);
                window = Process.Start(run)!;
                window.StandardInput.Write("ADA\n");
                window.StandardInput.Close();
                editor.Answer(ask.RootElement, success: true, new { processId = window.Id });
            }
            Task<string> screen = window.StandardOutput.ReadToEndAsync();
            Task<string> errors = window.StandardError.ReadToEndAsync();

            editor.WaitForEvent("initialized").Dispose();
            using (JsonDocument launched = editor.WaitForResponse("launch"))
                Assert.True(launched.RootElement.GetProperty("success").GetBoolean(), launched.RootElement.ToString());
            editor.Request("setBreakpoints", new
            {
                source = new { path = prog },
                breakpoints = new[] { new { line = 3 } },
            });
            using (JsonDocument bp = editor.WaitForResponse("setBreakpoints"))
                Assert.True(bp.RootElement.GetProperty("body").GetProperty("breakpoints")[0]
                    .GetProperty("verified").GetBoolean());
            editor.Request("configurationDone");
            editor.WaitForResponse("configurationDone").Dispose();

            using (JsonDocument stopped = editor.WaitForEvent("stopped"))
                Assert.Equal("breakpoint", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
            editor.Request("stackTrace", new { threadId = 1 });
            using (JsonDocument st = editor.WaitForResponse("stackTrace"))
            {
                JsonElement top = st.RootElement.GetProperty("body").GetProperty("stackFrames")[0];
                Assert.Equal("MAIN", top.GetProperty("name").GetString());
                Assert.Equal(3, top.GetProperty("line").GetInt32());
            }
            editor.Request("continue", new { threadId = 1 });
            using (JsonDocument exited = editor.WaitForEvent("exited"))
                Assert.Equal(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
            editor.WaitForEvent("terminated").Dispose();
            editor.Request("disconnect");
            editor.WaitForResponse("disconnect").Dispose();

            Assert.True(window.WaitForExit(30_000), "the program's process did not end");
            Assert.Contains("name? HELLO ADA", (await screen).Replace("\r\n", "\n") + await errors);
        }
        finally
        {
            adapter.StandardInput.Close();
            if (!adapter.WaitForExit(10_000)) adapter.Kill(entireProcessTree: true);
            if (window != null && !window.HasExited) window.Kill(entireProcessTree: true);
        }
        Assert.Equal(0, adapter.ExitCode);
        Assert.Equal("", await adapterErrors);
    }

    [Fact]
    public void TheProgramsProcessRefusesToStartWithNoAdapterToReach()
    {
        string dir = Folder();
        string prog = Program(dir, "plain.shoddy", "Def Main()\n    Print(1)\n");
        var psi = new ProcessStartInfo("dotnet", $"\"{TestHost.MillDll}\" run --perch 1 \"{prog}\"")
        {
            WorkingDirectory = dir,
        };
        var (exit, said) = ProcessRun.CaptureAll(psi);
        Assert.Equal(2, exit);
        Assert.Contains("mill dap", said);
    }

    // ---- the editor's end -------------------------------------------------

    /// <summary>An adapter in this process, reached over a loopback
    /// connection standing in for its stdin and stdout, and the editor
    /// talking to it.</summary>
    static (PerchAdapter Adapter, Editor Editor, Task<int> Serving) InProcess(
        IReadOnlyDictionary<string, string>? env = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var editorSide = new TcpClient();
        editorSide.Connect(IPAddress.Loopback, port);
        TcpClient adapterSide = listener.AcceptTcpClient();
        listener.Stop();
        NetworkStream a = adapterSide.GetStream();
        var adapter = new PerchAdapter(a, a, new[] { "mill" }, env) { StartTimeout = TimeSpan.FromSeconds(10) };
        Task<int> serving = Task.Run(() =>
        {
            using (adapterSide) return adapter.Serve();
        });
        NetworkStream e = editorSide.GetStream();
        return (adapter, new Editor(e, e), serving);
    }

    /// <summary>The window's mill, played in this process: a perch that
    /// connects back to the adapter and compiles what the launch names.</summary>
    static Task<int> PerchInThisProcess(int port) => Task.Run(() =>
    {
        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        NetworkStream s = tcp.GetStream();
        var perch = new PerchServer(s, s)
        {
            Launcher = program => Weaver.LoadDebug(Parser.Parse(Lexer.ReadProgram(program))),
        };
        return perch.Serve();
    });

    /// <summary>A session's result, or a failure if it never ends.</summary>
    static Task<int> Done(Task<int> task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>The editor's end of the protocol: requests out, every
    /// message in, and the adapter's own requests answered by the test.</summary>
    sealed class Editor : IDisposable
    {
        readonly Stream input, output;
        int seq;

        public Editor(Stream input, Stream output)
        {
            this.input = input;
            this.output = output;
        }

        public void Request(string command, object? args = null) =>
            DapWire.Write(output, DapWire.Body(new { seq = ++seq, type = "request", command, arguments = args }));

        public void Answer(JsonElement request, bool success, object? body = null, string? message = null) =>
            DapWire.Write(output, DapWire.Body(new
            {
                seq = ++seq,
                type = "response",
                request_seq = request.GetProperty("seq").GetInt32(),
                command = request.GetProperty("command").GetString(),
                success,
                message,
                body,
            }));

        public JsonDocument WaitForRequest(string command) => WaitFor(m =>
            m.GetProperty("type").GetString() == "request" && m.GetProperty("command").GetString() == command);

        public JsonDocument WaitForResponse(string command) => WaitFor(m =>
            m.GetProperty("type").GetString() == "response" && m.GetProperty("command").GetString() == command);

        public JsonDocument WaitForEvent(string name) => WaitFor(m =>
            m.GetProperty("type").GetString() == "event" && m.GetProperty("event").GetString() == name);

        JsonDocument WaitFor(Func<JsonElement, bool> want)
        {
            for (int i = 0; i < 200; i++)
            {
                byte[]? body = DapWire.Read(input);
                if (body == null) throw new IOException("the adapter closed the session");
                JsonDocument msg = JsonDocument.Parse(body);
                if (want(msg.RootElement)) return msg;
                msg.Dispose();
            }
            throw new TimeoutException("expected message never arrived");
        }

        public void Dispose()
        {
            output.Dispose();
            input.Dispose();
        }
    }
}
