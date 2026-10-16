// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Shoddy.Perch;

/// <summary>
/// The adapter an editor runs: `mill dap`, speaking the Debug Adapter
/// Protocol over this process's stdin and stdout, as VS Code runs any
/// debug adapter. It is not the debugger. The debugger is the perch
/// inside the program's own process, and that process runs in a
/// terminal window of the operating system's own, so the program has
/// that window's keyboard and screen exactly as under `mill run`. The
/// adapter is what puts it there and relays to it:
///
///   initialize   answered here, with the perch's capabilities.
///   launch       the adapter listens on a free loopback port and asks
///                the editor for a terminal window (runInTerminal, kind
///                "external") running `mill run --perch PORT FILE` in
///                the program's folder. The mill in the window connects
///                back to the port; the adapter accepts, replays
///                initialize and launch to it, and from then on relays
///                every message both ways unchanged until the
///                connection closes. With noDebug the window runs
///                `mill run FILE args` and the session ends; the window
///                stays.
///   attach       the adapter connects to a host's perch at host:port
///                and relays the same way.
///
/// The split is the Python debugger's: an adapter process the editor
/// owns, a debuggee process the user sees. The debuggee connecting back,
/// rather than the adapter polling a port the debuggee will listen on,
/// is what makes the start a handshake and not a race.
/// </summary>
public sealed class PerchAdapter
{
    readonly Stream input;
    readonly Stream output;
    readonly object writeLock = new();
    readonly IReadOnlyList<string> mill;
    readonly IReadOnlyDictionary<string, string> env;
    int seq;

    byte[]? initialize;                     // the editor's initialize, replayed to the debuggee
    int initializeSeq;
    bool supportsTerminal;
    bool noDebug;
    Stream? debuggee;                       // the connection to the perch, once reached
    volatile bool ended;                    // no debuggee to relay to, now or ever
    volatile bool told;                     // the editor has had the terminated event
    readonly Queue<byte[]> held = new();    // the editor's messages read while awaiting its answer

    /// <summary>How long the mill has, once its window is open, to
    /// connect. The first launch compiles the program; nothing builds
    /// the library any more, so this is seconds, not minutes.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <param name="input">The editor's messages, in.</param>
    /// <param name="output">This adapter's messages, out.</param>
    /// <param name="mill">How to start a mill: the command and whatever
    /// precedes its verb, <c>["/path/mill"]</c> or
    /// <c>["dotnet", "/path/mill.dll"]</c>.</param>
    /// <param name="env">What the window's mill must inherit from this
    /// one. The editor opens the window with the editor's own
    /// environment, which need not name the library.</param>
    public PerchAdapter(Stream input, Stream output, IReadOnlyList<string> mill,
                        IReadOnlyDictionary<string, string>? env = null)
    {
        this.input = input;
        this.output = output;
        this.mill = mill;
        this.env = env ?? new Dictionary<string, string>();
    }

    /// <summary>Serve one session. Returns when the editor disconnects
    /// or closes its end.</summary>
    public int Serve()
    {
        while (true)
        {
            byte[]? body = held.Count > 0 ? held.Dequeue() : ReadFromEditor();
            if (body == null) return 0;
            if (debuggee != null && !ended)
            {
                try
                {
                    DapWire.Write(debuggee, body);
                    continue;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    End();
                }
            }
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement m = doc.RootElement;
            if (m.GetProperty("type").GetString() != "request") continue;
            string command = m.GetProperty("command").GetString()!;
            int requestSeq = m.GetProperty("seq").GetInt32();
            JsonElement args = m.TryGetProperty("arguments", out JsonElement a) ? a : default;
            if (ended)
            {
                // A request with no debuggee left to answer it. The editor
                // ends every session with disconnect and waits for the
                // reply, so that one succeeds. Without debugging there was
                // never a debuggee, and the program is running in its
                // window, so anything else succeeds too; refusing it would
                // put an error in front of a program that started fine.
                bool ok = command == "disconnect" || noDebug;
                Respond(requestSeq, command, success: ok, message: ok ? null : "the program has ended");
                if (command == "disconnect") return 0;
                continue;
            }
            switch (command)
            {
                case "initialize":
                    initialize = body;
                    initializeSeq = requestSeq;
                    supportsTerminal = Flag(args, "supportsRunInTerminalRequest");
                    Respond(requestSeq, command, PerchServer.Capabilities);
                    break;
                case "launch":
                    Launch(requestSeq, body, args);
                    break;
                case "attach":
                    Attach(requestSeq, body, args);
                    break;
                case "disconnect":
                    Respond(requestSeq, command);
                    return 0;
                default:
                    Respond(requestSeq, command, success: false, message: "no program has been launched");
                    break;
            }
        }
    }

    void Launch(int requestSeq, byte[] body, JsonElement args)
    {
        if (initialize == null)
        {
            Respond(requestSeq, "launch", success: false, message: "launch before initialize");
            return;
        }
        if (!supportsTerminal)
        {
            Fail(requestSeq, "launch",
                "this editor cannot open a terminal window, and a Shoddy program being debugged runs in one");
            return;
        }
        string? named = Text(args, "program");
        if (string.IsNullOrEmpty(named))
        {
            Fail(requestSeq, "launch", "the launch names no program");
            return;
        }
        string program = Path.GetFullPath(named);
        string cwd = Text(args, "cwd") is { Length: > 0 } c ? Path.GetFullPath(c) : Path.GetDirectoryName(program)!;
        string name = Path.GetFileName(program);
        noDebug = Flag(args, "noDebug");

        if (noDebug)
        {
            // The window is the whole of the job: the program runs there
            // under `mill run`, exactly as at a command line, and there
            // is no debugger to talk to.
            var run = new List<string>(mill) { "run" };
            if (Flag(args, "allowNet")) run.Add("--allow-net");
            run.Add(program);
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("args", out JsonElement pa) && pa.ValueKind == JsonValueKind.Array)
                foreach (JsonElement x in pa.EnumerateArray())
                    run.Add(x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString());
            (bool ok, string? why, _) = RunInTerminal($"shoddy run: {name}", cwd, run);
            if (!ok)
            {
                Fail(requestSeq, "launch", why);
                return;
            }
            Respond(requestSeq, "launch");
            End();
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        TcpClient? connection;
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var debug = new List<string>(mill) { "run", "--perch", port.ToString(), program };
            (bool ok, string? why, int? pid) = RunInTerminal($"shoddy debug: {name}", cwd, debug);
            if (!ok)
            {
                Fail(requestSeq, "launch", why);
                return;
            }
            connection = Accept(listener, pid, out string? reason);
            if (connection == null)
            {
                Fail(requestSeq, "launch", reason);
                return;
            }
        }
        finally
        {
            listener.Stop();
        }
        Relay(connection.GetStream(), body);
    }

    void Attach(int requestSeq, byte[] body, JsonElement args)
    {
        if (initialize == null)
        {
            Respond(requestSeq, "attach", success: false, message: "attach before initialize");
            return;
        }
        string host = Text(args, "host") is { Length: > 0 } h ? h : "127.0.0.1";
        int port = args.ValueKind == JsonValueKind.Object &&
                   args.TryGetProperty("port", out JsonElement p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;
        var client = new TcpClient();
        try
        {
            client.Connect(host, port);
        }
        catch (SocketException e)
        {
            client.Dispose();
            Fail(requestSeq, "attach",
                $"nothing is serving the debugger at {host}:{port} ({e.Message}); " +
                "a host serves it on the port SHODDY_PERCH names");
            return;
        }
        Relay(client.GetStream(), body);
    }

    /// <summary>Ask the editor for a terminal window of the operating
    /// system's own, started in <paramref name="cwd"/>, running
    /// <paramref name="args"/>, with this adapter's library environment
    /// added. Answers whether it opened, why not, and the process the
    /// editor reports when it reports one.</summary>
    (bool Ok, string? Why, int? Pid) RunInTerminal(string title, string cwd, IReadOnlyList<string> args)
    {
        int mine = Interlocked.Increment(ref seq);
        Send(new
        {
            seq = mine,
            type = "request",
            command = "runInTerminal",
            arguments = new { kind = "external", title, cwd, args, env = env.Count > 0 ? env : null },
        });
        // The editor answers in its own time; whatever else it sends
        // meanwhile is kept for after.
        while (true)
        {
            byte[]? body = ReadFromEditor();
            if (body == null) return (false, "the editor closed the session", null);
            using (JsonDocument doc = JsonDocument.Parse(body))
            {
                JsonElement m = doc.RootElement;
                if (m.GetProperty("type").GetString() == "response" &&
                    m.TryGetProperty("request_seq", out JsonElement rs) && rs.GetInt32() == mine)
                {
                    if (!Flag(m, "success"))
                        return (false, "the editor could not open a terminal window for the program: " +
                                       (Text(m, "message") ?? "it gave no reason"), null);
                    int? pid = m.TryGetProperty("body", out JsonElement b) && b.ValueKind == JsonValueKind.Object &&
                               b.TryGetProperty("processId", out JsonElement p) && p.ValueKind == JsonValueKind.Number
                        ? p.GetInt32() : null;
                    return (true, null, pid);
                }
            }
            held.Enqueue(body);
        }
    }

    /// <summary>The window is open: wait for the mill in it to connect,
    /// until it does, the process the editor named is gone, or
    /// <see cref="StartTimeout"/> has passed.</summary>
    TcpClient? Accept(TcpListener listener, int? pid, out string? reason)
    {
        DateTime deadline = DateTime.UtcNow + StartTimeout;
        while (!listener.Pending())
        {
            if (pid is int p && !Alive(p))
            {
                reason = "the mill ended before its debugger started; its window says why";
                return null;
            }
            if (DateTime.UtcNow > deadline)
            {
                reason = $"the mill did not start its debugger within {StartTimeout.TotalSeconds:0} seconds";
                return null;
            }
            Thread.Sleep(50);
        }
        reason = null;
        return listener.AcceptTcpClient();
    }

    static bool Alive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (Exception) { return true; }      // not ours to inspect: taken as living
    }

    /// <summary>The debuggee is reached: replay the editor's initialize
    /// and this request to it, and relay from here on. The perch answers
    /// initialize for itself; the editor has had its answer, so that one
    /// response is dropped on the way back.</summary>
    void Relay(Stream connection, byte[] request)
    {
        debuggee = connection;
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    byte[]? body;
                    try { body = DapWire.Read(connection); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException) { body = null; }
                    if (body == null) break;
                    using JsonDocument doc = JsonDocument.Parse(body);
                    JsonElement m = doc.RootElement;
                    string? type = m.GetProperty("type").GetString();
                    if (type == "response" && m.GetProperty("command").GetString() == "initialize" &&
                        m.TryGetProperty("request_seq", out JsonElement rs) && rs.GetInt32() == initializeSeq)
                        continue;
                    if (type == "event" && m.GetProperty("event").GetString() == "terminated") told = true;
                    try
                    {
                        lock (writeLock) DapWire.Write(output, body);
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                        break;          // the editor is gone
                    }
                }
            }
            finally
            {
                End();
            }
        }) { IsBackground = true, Name = "shoddy-perch-relay" }.Start();
        try
        {
            DapWire.Write(connection, initialize!);
            DapWire.Write(connection, request);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            End();
        }
    }

    /// <summary>The session is over. If the debuggee did not say so
    /// itself (it was killed, or its window was closed), say so for it,
    /// or the editor would wait for it forever.</summary>
    void End()
    {
        lock (writeLock)
        {
            if (ended) return;
            ended = true;
            try { debuggee?.Dispose(); } catch (Exception) { /* already gone */ }
            if (told) return;
            told = true;
        }
        try
        {
            Event("terminated");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // the editor is gone too
        }
    }

    /// <summary>Refuse a request and end the session.</summary>
    void Fail(int requestSeq, string command, string? why)
    {
        Respond(requestSeq, command, success: false, message: why);
        End();
    }

    byte[]? ReadFromEditor()
    {
        try { return DapWire.Read(input); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return null; }
    }

    void Send(object payload)
    {
        byte[] body = DapWire.Body(payload);
        lock (writeLock) DapWire.Write(output, body);
    }

    void Respond(int requestSeq, string command, object? body = null, bool success = true, string? message = null) =>
        Send(new
        {
            seq = Interlocked.Increment(ref seq),
            type = "response",
            request_seq = requestSeq,
            command,
            success,
            message,
            body,
        });

    void Event(string name, object? body = null) =>
        Send(new { seq = Interlocked.Increment(ref seq), type = "event", @event = name, body });

    static bool Flag(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out JsonElement v) &&
        v.ValueKind == JsonValueKind.True;

    static string? Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out JsonElement v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
