using System.Text;
using System.Text.Json;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Mill;

/// <summary>
/// The perch: Shoddy-native debugging over the Debug Adapter Protocol.
/// VS Code launches `mill dap`, sends a program path, and this server
/// compiles it with debug instrumentation, runs it on a worker thread,
/// and pauses it inside Engine line hooks — breakpoints, stepping, a
/// call stack of defs, Shoddy-shaped variables, and the value stack.
/// </summary>
public sealed class DapServer : IDebugSink
{
    readonly Stream input = Console.OpenStandardInput();
    readonly Stream output = Console.OpenStandardOutput();
    readonly object writeLock = new();
    int seq;

    // breakpoints per normalized absolute path
    readonly Dictionary<string, HashSet<int>> bps = new(StringComparer.OrdinalIgnoreCase);

    enum Mode { Run, Entry, StepIn, StepOver, StepOut }
    volatile Mode mode = Mode.Run;
    int stepDepth;
    readonly SemaphoreSlim gate = new(0);
    Engine? paused;                     // valid only while stopped
    readonly List<object> varRefs = new();

    Func<TextWriter, TextReader, int>? entry;
    bool stopOnEntry;

    public static int Serve() => new DapServer().Run();

    int Run()
    {
        while (true)
        {
            JsonDocument? msg = ReadMessage();
            if (msg == null) return 0;
            using (msg)
            {
                JsonElement root = msg.RootElement;
                if (root.GetProperty("type").GetString() != "request") continue;
                Dispatch(root);
            }
        }
    }

    // ---- protocol plumbing --------------------------------------------

    JsonDocument? ReadMessage()
    {
        int len = -1;
        var line = new StringBuilder();
        while (true)                    // headers
        {
            int c = input.ReadByte();
            if (c < 0) return null;
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
        if (len < 0) return null;
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            int n = input.Read(buf, got, len - got);
            if (n <= 0) return null;
            got += n;
        }
        return JsonDocument.Parse(buf);
    }

    void Send(object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (writeLock)
        {
            output.Write(head);
            output.Write(body);
            output.Flush();
        }
    }

    void Respond(JsonElement req, object? body = null, bool success = true, string? message = null) =>
        Send(new
        {
            seq = ++seq,
            type = "response",
            request_seq = req.GetProperty("seq").GetInt32(),
            command = req.GetProperty("command").GetString(),
            success,
            message,
            body,
        });

    void Event(string name, object? body = null) =>
        Send(new { seq = ++seq, type = "event", @event = name, body });

    // ---- request dispatch ---------------------------------------------

    void Dispatch(JsonElement req)
    {
        string cmd = req.GetProperty("command").GetString()!;
        JsonElement args = req.TryGetProperty("arguments", out JsonElement a) ? a : default;
        switch (cmd)
        {
            case "initialize":
                Respond(req, new
                {
                    supportsConfigurationDoneRequest = true,
                    supportsEvaluateForHovers = true,
                });
                Event("initialized");
                break;

            case "launch":
            {
                string program = Path.GetFullPath(args.GetProperty("program").GetString()!);
                stopOnEntry = args.TryGetProperty("stopOnEntry", out JsonElement se) && se.GetBoolean();
                try
                {
                    var prog = Parser.Parse(Lexer.ReadProgram(program));
                    entry = Weaver.LoadDebug(prog);
                    Respond(req);
                }
                catch (ShoddyError e)
                {
                    string where = e.Line > 0 ? $" (line {e.Line})" : "";
                    Respond(req, success: false, message: $"ERROR{where}: {e.Message}");
                    Event("terminated");
                }
                break;
            }

            case "setBreakpoints":
            {
                string path = Path.GetFullPath(args.GetProperty("source").GetProperty("path").GetString()!);
                var lines = new HashSet<int>();
                var verified = new List<object>();
                if (args.TryGetProperty("breakpoints", out JsonElement arr))
                    foreach (JsonElement b in arr.EnumerateArray())
                    {
                        int ln = b.GetProperty("line").GetInt32();
                        lines.Add(ln);
                        verified.Add(new { verified = true, line = ln });
                    }
                bps[path] = lines;
                Respond(req, new { breakpoints = verified });
                break;
            }

            case "setExceptionBreakpoints":
                Respond(req, new { breakpoints = Array.Empty<object>() });
                break;

            case "configurationDone":
                Respond(req);
                StartProgram();
                break;

            case "threads":
                Respond(req, new { threads = new[] { new { id = 1, name = "main" } } });
                break;

            case "stackTrace":
                Respond(req, new { stackFrames = StackFrames(), totalFrames = paused?.Frames.Count ?? 0 });
                break;

            case "scopes":
            {
                int frameId = args.GetProperty("frameId").GetInt32();
                Respond(req, new
                {
                    scopes = new object[]
                    {
                        new { name = "Locals", variablesReference = NewRef(("locals", frameId)), expensive = false },
                        new { name = "Globals", variablesReference = NewRef(("globals", 0)), expensive = false },
                        new { name = "Value Stack", variablesReference = NewRef(("stack", 0)), expensive = false },
                    },
                });
                break;
            }

            case "variables":
                Respond(req, new { variables = Variables(args.GetProperty("variablesReference").GetInt32()) });
                break;

            case "continue":
                Resume(req, Mode.Run);
                Respond(req, new { allThreadsContinued = true });
                break;
            case "next":
                Resume(req, Mode.StepOver);
                Respond(req);
                break;
            case "stepIn":
                Resume(req, Mode.StepIn);
                Respond(req);
                break;
            case "stepOut":
                Resume(req, Mode.StepOut);
                Respond(req);
                break;

            case "evaluate":
                Evaluate(req, args);
                break;

            case "disconnect":
                Respond(req);
                Environment.Exit(0);
                break;

            default:
                Respond(req, success: false, message: $"unsupported: {cmd}");
                break;
        }
    }

    // ---- running the program ------------------------------------------

    void StartProgram()
    {
        if (entry == null) return;
        mode = stopOnEntry ? Mode.Entry : Mode.Run;
        var stdout = new EventWriter(this, "stdout");
        var stderr = new EventWriter(this, "stderr");
        Task.Run(() =>
        {
            int rc;
            TextWriter savedErr = Console.Error;
            Console.SetError(stderr);           // woven error reports -> debug console
            try
            {
                Engine.PendingSink = this;
                rc = entry(stdout, TextReader.Null);
            }
            catch (Exception e)
            {
                stderr.WriteLine($"perch: {e.Message}");
                rc = 1;
            }
            finally
            {
                Engine.PendingSink = null;
                Console.SetError(savedErr);
            }
            stdout.FlushBuf();
            stderr.FlushBuf();
            Event("exited", new { exitCode = rc });
            Event("terminated");
        });
    }

    void Resume(JsonElement req, Mode m)
    {
        if (paused == null) return;
        stepDepth = paused.Frames.Count;
        mode = m;
        paused = null;
        varRefs.Clear();
        gate.Release();
    }

    /// <summary>The line hook — runs on the program thread and blocks it
    /// while the front-end inspects state.</summary>
    public void OnLine(Engine rt)
    {
        string? reason = mode switch
        {
            Mode.Entry => "entry",
            Mode.StepIn => "step",
            Mode.StepOver when rt.Frames.Count <= stepDepth => "step",
            Mode.StepOut when rt.Frames.Count < stepDepth => "step",
            _ => null,
        };
        if (reason == null && HitsBreakpoint(rt)) reason = "breakpoint";
        if (reason == null) return;

        paused = rt;
        varRefs.Clear();
        Event("stopped", new { reason, threadId = 1, allThreadsStopped = true });
        gate.Wait();
    }

    bool HitsBreakpoint(Engine rt)
    {
        DebugFrame f = rt.Frames[^1];
        if (f.FileId >= rt.DebugFiles.Length) return false;
        return bps.TryGetValue(Path.GetFullPath(rt.DebugFiles[f.FileId]), out HashSet<int>? lines) &&
               lines.Contains(f.Line);
    }

    // ---- state views ---------------------------------------------------

    object[] StackFrames()
    {
        if (paused == null) return Array.Empty<object>();
        Engine rt = paused;
        var frames = new List<object>();
        for (int k = rt.Frames.Count - 1; k >= 0; k--)
        {
            DebugFrame f = rt.Frames[k];
            string? path = f.FileId < rt.DebugFiles.Length ? Path.GetFullPath(rt.DebugFiles[f.FileId]) : null;
            frames.Add(new
            {
                id = k,
                name = f.Name,
                line = f.Line,
                column = 1,
                source = path == null ? null : new { name = Path.GetFileName(path), path },
            });
        }
        return frames.ToArray();
    }

    int NewRef(object o)
    {
        varRefs.Add(o);
        return varRefs.Count;          // 1-based; 0 means "no children"
    }

    List<object> Variables(int reference)
    {
        var vars = new List<object>();
        if (paused == null || reference < 1 || reference > varRefs.Count) return vars;
        Engine rt = paused;
        switch (varRefs[reference - 1])
        {
            case ("locals", int frameId) when frameId < rt.Frames.Count:
            {
                // last-wins per name, shown in first-bound order
                var seen = new Dictionary<string, Value>();
                var order = new List<string>();
                foreach ((string name, Value v) in rt.Frames[frameId].Binds)
                {
                    if (!seen.ContainsKey(name)) order.Add(name);
                    seen[name] = v;
                }
                foreach (string name in order)
                    vars.Add(Var(name, seen[name]));
                break;
            }
            case ("globals", _):
                foreach ((string name, Value v) in rt.Globals)
                    vars.Add(Var(name, v));
                break;
            case ("stack", _):
                for (int k = rt.StackView.Count - 1; k >= 0; k--)
                    vars.Add(Var(k == rt.StackView.Count - 1 ? "top" : $"#{k + 1}", rt.StackView[k]));
                break;
            case Value v:
                switch (v.T)
                {
                    case VType.Rec:
                        for (int k = 0; k < v.Elems!.Length; k++)
                            vars.Add(Var(v.RType!.FDisp[k], v.Elems[k]));
                        break;
                    case VType.Arr:
                        for (int k = 0; k < v.Elems!.Length; k++)
                            vars.Add(Var($"[{k + 1}]", v.Elems[k]));
                        break;
                    case VType.Quot:
                        for (int k = 0; k < v.CItems!.Length; k++)
                        {
                            QItem it = v.CItems[k];
                            if (it.Lit != null) vars.Add(Var($"[{k + 1}]", it.Lit));
                            else vars.Add(new { name = $"[{k + 1}]", value = it.Disp ?? "?", variablesReference = 0 });
                        }
                        break;
                }
                break;
        }
        return vars;
    }

    object Var(string name, Value v)
    {
        var sw = new StringWriter();
        Printer.Repr(sw, v);
        bool composite = v.T is VType.Rec or VType.Arr ||
                         (v.T == VType.Quot && v.CItems!.Length > 0);
        return new
        {
            name,
            value = sw.ToString(),
            type = Value.TypeName(v.T),
            variablesReference = composite ? NewRef(v) : 0,
        };
    }

    void Evaluate(JsonElement req, JsonElement args)
    {
        string expr = args.GetProperty("expression").GetString()!.Trim().ToUpperInvariant();
        Engine? rt = paused;
        if (rt == null) { Respond(req, success: false, message: "not paused"); return; }
        int frameId = args.TryGetProperty("frameId", out JsonElement fe) ? fe.GetInt32() : rt.Frames.Count - 1;

        Value? found = null;
        if (frameId < rt.Frames.Count)
            for (int k = rt.Frames[frameId].Binds.Count - 1; k >= 0; k--)
                if (rt.Frames[frameId].Binds[k].Name == expr) { found = rt.Frames[frameId].Binds[k].V; break; }
        if (found == null)
            for (int k = rt.Globals.Count - 1; k >= 0; k--)
                if (rt.Globals[k].Name == expr) { found = rt.Globals[k].V; break; }
        if (found == null)
        {
            Respond(req, success: false, message: $"unknown name: {expr}");
            return;
        }
        var sw = new StringWriter();
        Printer.Repr(sw, found);
        bool composite = found.T is VType.Rec or VType.Arr ||
                         (found.T == VType.Quot && found.CItems!.Length > 0);
        Respond(req, new { result = sw.ToString(), variablesReference = composite ? NewRef(found) : 0 });
    }

    /// <summary>Forwards program output to the client as output events,
    /// line-buffered.</summary>
    sealed class EventWriter : TextWriter
    {
        readonly DapServer server;
        readonly string category;
        readonly StringBuilder buf = new();

        public EventWriter(DapServer server, string category)
        {
            this.server = server;
            this.category = category;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char c)
        {
            lock (buf)
            {
                buf.Append(c);
                if (c == '\n') FlushLocked();
            }
        }

        public override void Write(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (char c in s) Write(c);
        }

        public override void Flush() => FlushBuf();

        public void FlushBuf()
        {
            lock (buf) FlushLocked();
        }

        void FlushLocked()
        {
            if (buf.Length == 0) return;
            server.Event("output", new { category, output = buf.ToString() });
            buf.Clear();
        }
    }
}
