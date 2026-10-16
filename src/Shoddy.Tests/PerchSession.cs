// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Shoddy.Tests;

/// <summary>
/// One debugged program and the protocol client talking to it, the way
/// the adapter does: this test listens on a loopback port, starts
/// `mill run --perch PORT FILE` in the program's folder with the mill
/// this test host carries, accepts the connection the mill makes back,
/// and speaks the protocol over it. The process's stdout and stderr are
/// its screen, its stdin its keyboard.
///
/// SHODDYLIB pins the sources to the checkout's own machines/, so a
/// breakpoint set in a machine names the file in the tree; a test that
/// installs a library elsewhere names that instead.
/// </summary>
sealed class PerchSession : IDisposable
{
    static readonly string Root = RepoRoot.Dir;

    readonly Process proc;
    readonly TcpClient tcp;
    readonly NetworkStream stream;
    readonly StringBuilder screen = new();
    readonly StringBuilder console = new();     // Debug Console output events
    readonly Task outPump, errPump;
    int seq;
    int? exitCode;

    PerchSession(Process proc, TcpClient tcp)
    {
        this.proc = proc;
        this.tcp = tcp;
        stream = tcp.GetStream();
        stream.ReadTimeout = 120_000;       // a hang fails rather than wedges
        outPump = Pump(proc.StandardOutput);
        errPump = Pump(proc.StandardError);
    }

    Task Pump(StreamReader r) => Task.Run(async () =>
    {
        var buf = new char[4096];
        int n;
        while ((n = await r.ReadAsync(buf)) > 0)
            lock (screen) screen.Append(buf, 0, n);
    });

    /// <summary>Start <paramref name="program"/>'s process in
    /// <paramref name="cwd"/>, with <paramref name="stdin"/> as its
    /// keyboard, and the session with it: initialized, nothing launched
    /// yet.</summary>
    public static PerchSession Start(string cwd, string program, string? stdin = null,
                                     IReadOnlyDictionary<string, string>? env = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var psi = new ProcessStartInfo("dotnet", $"\"{TestHost.MillDll}\" run --perch {port} \"{program}\"")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SHODDYLIB"] = Path.Combine(Root, "machines");
        psi.Environment.Remove("SHODDYLIBBIN");
        if (env != null)
            foreach ((string name, string value) in env) psi.Environment[name] = value;
        Process p = Process.Start(psi)!;
        if (stdin != null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();

        // The mill connects back once it is up. One that dies first says
        // why on its screen, which is the only diagnostic there is.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        try
        {
            while (!listener.Pending())
            {
                if (p.HasExited)
                    throw new InvalidOperationException(
                        $"the mill exited with {p.ExitCode} before serving the debugger: " +
                        p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
                if (DateTime.UtcNow > deadline)
                {
                    p.Kill(entireProcessTree: true);
                    throw new TimeoutException("the mill did not connect back within 60 seconds");
                }
                Thread.Sleep(50);
            }
            var s = new PerchSession(p, listener.AcceptTcpClient());
            s.Request("initialize", new { adapterID = "shoddy" });
            s.WaitForEvent("initialized").Dispose();
            return s;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Launch(string program, object? extra = null)
    {
        var args = new Dictionary<string, object?> { ["program"] = program };
        if (extra != null)
            foreach (var p in extra.GetType().GetProperties())
                args[p.Name] = p.GetValue(extra);
        Request("launch", args);
        using JsonDocument launched = WaitForResponse("launch");
        Assert.True(launched.RootElement.GetProperty("success").GetBoolean(),
            launched.RootElement.ToString());
    }

    /// <summary>Replace the breakpoints in one file.</summary>
    public void Breakpoints(string file, params object[] breakpoints)
    {
        Request("setBreakpoints", new { source = new { path = file }, breakpoints });
        using JsonDocument r = WaitForResponse("setBreakpoints");
        foreach (JsonElement b in r.RootElement.GetProperty("body").GetProperty("breakpoints").EnumerateArray())
            Assert.True(b.GetProperty("verified").GetBoolean(), r.RootElement.ToString());
    }

    /// <summary>An expression's result in a frame, or null when the
    /// debugger refused it.</summary>
    public string? Evaluate(int frameId, string expression)
    {
        Request("evaluate", new { expression, frameId, context = "repl" });
        using JsonDocument r = WaitForResponse("evaluate");
        return r.RootElement.GetProperty("success").GetBoolean()
            ? r.RootElement.GetProperty("body").GetProperty("result").GetString()
            : null;
    }

    /// <summary>A frame's scopes' references: Locals, Globals, Value Stack.</summary>
    public int[] Scopes(int frameId)
    {
        Request("scopes", new { frameId });
        using JsonDocument sc = WaitForResponse("scopes");
        return sc.RootElement.GetProperty("body").GetProperty("scopes").EnumerateArray()
            .Select(x => x.GetProperty("variablesReference").GetInt32()).ToArray();
    }

    public Dictionary<string, (string Value, int Reference)> Variables(int reference)
    {
        Request("variables", new { variablesReference = reference });
        using JsonDocument vs = WaitForResponse("variables");
        return vs.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!,
                          v => (v.GetProperty("value").GetString()!, v.GetProperty("variablesReference").GetInt32()));
    }

    /// <summary>Set a variable; answers its new value as shown.</summary>
    public string SetVariable(int reference, string name, string value)
    {
        Request("setVariable", new { variablesReference = reference, name, value });
        using JsonDocument r = WaitForResponse("setVariable");
        Assert.True(r.RootElement.GetProperty("success").GetBoolean(), r.RootElement.ToString());
        return r.RootElement.GetProperty("body").GetProperty("value").GetString()!;
    }

    /// <summary>What the debugger has written to the Debug Console.</summary>
    public string Console() => console.ToString();

    public void ConfigurationDone()
    {
        Request("configurationDone");
        WaitForResponse("configurationDone").Dispose();
    }

    public void Request(string command, object? args = null)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new { seq = ++seq, type = "request", command, arguments = args });
        byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        stream.Write(head);
        stream.Write(body);
        stream.Flush();
    }

    public JsonDocument WaitForEvent(string name) => WaitFor(m =>
        m.GetProperty("type").GetString() == "event" && m.GetProperty("event").GetString() == name);

    public JsonDocument WaitForResponse(string command) => WaitFor(m =>
        m.GetProperty("type").GetString() == "response" && m.GetProperty("command").GetString() == command);

    /// <summary>The exit code the session reports for the program.</summary>
    public int ExitCode()
    {
        if (exitCode is int known) return known;
        using JsonDocument exited = WaitForEvent("exited");
        exitCode = exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32();
        return exitCode.Value;
    }

    /// <summary>The innermost frame of the stopped program.</summary>
    public JsonElement TopFrame()
    {
        Request("stackTrace", new { threadId = 1 });
        using JsonDocument st = WaitForResponse("stackTrace");
        return st.RootElement.GetProperty("body").GetProperty("stackFrames")[0].Clone();
    }

    /// <summary>A frame's Locals scope, name to displayed value.</summary>
    public Dictionary<string, string> Locals(int frameId)
    {
        Request("scopes", new { frameId });
        int reference;
        using (JsonDocument sc = WaitForResponse("scopes"))
            reference = sc.RootElement.GetProperty("body").GetProperty("scopes")[0]
                .GetProperty("variablesReference").GetInt32();
        Request("variables", new { variablesReference = reference });
        using JsonDocument vs = WaitForResponse("variables");
        return vs.RootElement.GetProperty("body").GetProperty("variables").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString()!);
    }

    /// <summary>End the session and answer everything the program put
    /// on its screen.</summary>
    public string Finish()
    {
        Request("disconnect");
        if (!proc.WaitForExit(60_000)) proc.Kill(entireProcessTree: true);
        Task.WaitAll(outPump, errPump);
        lock (screen) return screen.ToString().Replace("\r\n", "\n");
    }

    JsonDocument WaitFor(Func<JsonElement, bool> want)
    {
        for (int i = 0; i < 500; i++)
        {
            JsonDocument msg = Read();
            JsonElement m = msg.RootElement;
            if (m.GetProperty("type").GetString() == "event" &&
                m.GetProperty("event").GetString() == "exited")
                exitCode = m.GetProperty("body").GetProperty("exitCode").GetInt32();
            if (m.GetProperty("type").GetString() == "event" &&
                m.GetProperty("event").GetString() == "output" &&
                m.GetProperty("body").GetProperty("category").GetString() == "console")
                console.Append(m.GetProperty("body").GetProperty("output").GetString());
            if (want(m)) return msg;
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
            if (c < 0) throw new IOException("transport closed: " + Screen());
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
            if (n <= 0) throw new IOException("transport closed: " + Screen());
            got += n;
        }
        return JsonDocument.Parse(buf);
    }

    string Screen()
    {
        lock (screen) return screen.ToString();
    }

    public void Dispose()
    {
        tcp.Dispose();
        if (!proc.HasExited)
        {
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);
        }
        proc.Dispose();
    }
}
