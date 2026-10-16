// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text;
using System.Text.Json;
using Shoddy.Runtime;

namespace Shoddy.Perch;

/// <summary>
/// The perch: Shoddy-native debugging over the Debug Adapter Protocol,
/// parameterized over a transport Stream pair — a debugged program
/// serves it over its connection back to the editor's adapter
/// (<see cref="PerchAdapter"/>), a host's debug build serves it over a
/// TCP connection, and a test serves it over a loopback pipe; the
/// server neither knows nor cares (A6.1).
///
/// Two ways in. LAUNCH: the client names a program, the
/// <see cref="Launcher"/> hook (the mill's — it owns compilation)
/// answers an entry point, and the server runs it on a worker thread.
/// ATTACH: the debuggee already lives in the host process; the server
/// installs itself as <see cref="Engine.PendingSink"/> so every engine
/// constructed while the client is attached reports its lines here.
/// Either way the program pauses inside Engine line hooks —
/// breakpoints, stepping, a call stack of defs, Shoddy-shaped
/// variables, and the value stack.
/// </summary>
public sealed class PerchServer : IDebugSink
{
    readonly Stream input;
    readonly Stream output;
    readonly object writeLock = new();
    int seq;

    /// <summary>Handles a `launch` request: given the program path,
    /// prepare and return its entry point (throwing ShoddyError on a
    /// bad program). The entry point takes the program's output, input
    /// and arguments. Null — an attach-only server, as compiled into a
    /// host — refuses launch by name.</summary>
    public Func<string, Func<TextWriter, TextReader, string[], int>>? Launcher { get; set; }

    /// <summary>After a launched program returns: the mill waits here
    /// for open scribbler windows, so "draw a picture, return" stays a
    /// complete program under the debugger.</summary>
    public Action? AfterRun { get; set; }

    /// <summary>
    /// Compiles and runs a Shoddy expression on the stopped engine with
    /// the given bindings in view (later ones shadow earlier ones),
    /// answering the values it left; throws ShoddyError with the
    /// language's own message. The launcher supplies it, since only the
    /// mill can compile (Shoddy.Compiler's DebugBuild.Evaluate). The Debug
    /// Console, watches, hovers, breakpoint conditions, logpoint messages
    /// and set-variable all go through it.
    ///
    /// <para>Null on a server compiled into a host, which carries no
    /// compiler: there an expression is answered when it names a
    /// binding.</para>
    /// </summary>
    public Func<Engine, IReadOnlyList<(string Name, Value V)>, string, Value[]>? Evaluator { get; set; }

    /// <summary>What this debugger can do, as the initialize response
    /// states it. The adapter answers initialize on the perch's behalf,
    /// before the program's process exists, so the one statement is
    /// here for both.</summary>
    public static readonly object Capabilities = new
    {
        supportsConfigurationDoneRequest = true,
        supportsEvaluateForHovers = true,
        supportsExceptionInfoRequest = true,
        supportsConditionalBreakpoints = true,
        supportsHitConditionalBreakpoints = true,
        supportsLogPoints = true,
        supportsSetVariable = true,
        exceptionBreakpointFilters = new[]
        {
            new { filter = ErrorsFilter, label = "Runtime errors", @default = true },
        },
    };

    /// <summary>One line breakpoint: where, and what it asks before it
    /// stops.</summary>
    sealed class Breakpoint
    {
        public string? Condition;       // stop only when this is True
        public HitRule? Hits;           // stop only on the hits it allows
        public string? LogMessage;      // log, with {expressions}, and never stop
        public int Count;               // hits so far
    }

    /// <summary>A hit-count condition: an operator and a whole number.
    /// A bare number means exactly that hit.</summary>
    sealed record HitRule(string Op, int N)
    {
        public bool Allows(int hit) => Op switch
        {
            ">" => hit > N,
            ">=" => hit >= N,
            "<" => hit < N,
            "<=" => hit <= N,
            "%" => N > 0 && hit % N == 0,
            _ => hit == N,
        };

        public static HitRule? Parse(string text)
        {
            string t = text.Trim();
            foreach (string op in new[] { ">=", "<=", "==", ">", "<", "=", "%" })
                if (t.StartsWith(op, StringComparison.Ordinal))
                    return int.TryParse(t[op.Length..].Trim(), out int n) && n >= 0
                        ? new HitRule(op is "==" or "=" ? "==" : op, n)
                        : null;
            return int.TryParse(t, out int bare) && bare >= 0 ? new HitRule("==", bare) : null;
        }
    }

    // breakpoints per normalized absolute path, then line; guarded by
    // their own lock, because the client replaces them while the program
    // thread reads them
    readonly Dictionary<string, Dictionary<int, Breakpoint>> bps = new(StringComparer.OrdinalIgnoreCase);

    enum Mode { Run, Entry, StepIn, StepOver, StepOut, Pause }
    volatile Mode mode = Mode.Run;
    int stepDepth;
    readonly SemaphoreSlim gate = new(0);
    Engine? paused;                     // valid only while stopped
    readonly List<object> varRefs = new();

    Func<TextWriter, TextReader, string[], int>? entry;
    string[] programArgs = Array.Empty<string>();
    bool stopOnEntry;

    // Runtime errors stop the program at the failing line (the
    // "errors" exception filter), on until the client says otherwise.
    volatile bool breakOnErrors = true;
    Exception? lastError;               // valid only while stopped on one
    bool attached;
    bool disconnected;

    public PerchServer(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    public int Serve()
    {
        try
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
                // Returning (rather than exiting) lets the transport's
                // owner tear down whatever the debugged program left.
                if (disconnected) return 0;
            }
        }
        finally
        {
            if (attached && ReferenceEquals(Engine.PendingSink, this))
                Engine.PendingSink = null;
            // A program paused on this server must not stay paused
            // forever after the client goes away.
            if (paused != null) { paused = null; gate.Release(); }
        }
    }

    // ---- protocol plumbing --------------------------------------------

    JsonDocument? ReadMessage()
    {
        byte[]? body = DapWire.Read(input);
        return body == null ? null : JsonDocument.Parse(body);
    }

    void Send(object payload)
    {
        byte[] body = DapWire.Body(payload);
        lock (writeLock) DapWire.Write(output, body);
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
                Respond(req, Capabilities);
                Event("initialized");
                break;

            case "launch":
            {
                if (Launcher == null)
                {
                    Respond(req, success: false,
                        message: "this debugger only attaches, because the host started the program");
                    Event("terminated");
                    break;
                }
                string program = Path.GetFullPath(args.GetProperty("program").GetString()!);
                stopOnEntry = args.TryGetProperty("stopOnEntry", out JsonElement se) && se.GetBoolean();
                // The program's own arguments, as `mill run FILE a b` gives
                // them: what Args() answers.
                if (args.TryGetProperty("args", out JsonElement pa) && pa.ValueKind == JsonValueKind.Array)
                    programArgs = pa.EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
                // The network is a gated capability; allowNet is the
                // launch's --allow-net. Set before the program's engine is
                // constructed, which is when the engine reads it.
                if (args.TryGetProperty("allowNet", out JsonElement an) && an.ValueKind == JsonValueKind.True)
                    Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", "1");
                try
                {
                    entry = Launcher(program);
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

            case "attach":
                // The debuggee is the host's own instrumented code:
                // every engine constructed while attached picks this
                // sink up at construction (the woven preamble and
                // Shoddy.Hosting both copy PendingSink).
                attached = true;
                stopOnEntry = false;
                Engine.PendingSink = this;
                Respond(req);
                break;

            case "setBreakpoints":
            {
                string path = Path.GetFullPath(args.GetProperty("source").GetProperty("path").GetString()!);
                var lines = new Dictionary<int, Breakpoint>();
                var verified = new List<object>();
                if (args.TryGetProperty("breakpoints", out JsonElement arr))
                    foreach (JsonElement b in arr.EnumerateArray())
                    {
                        // Every line binds, a machine's included: the
                        // debugger runs the instrumented weave of every
                        // machine the program reaches.
                        int ln = b.GetProperty("line").GetInt32();
                        var bp = new Breakpoint
                        {
                            Condition = NonEmpty(b, "condition"),
                            LogMessage = NonEmpty(b, "logMessage"),
                        };
                        string? hit = NonEmpty(b, "hitCondition");
                        if (hit != null)
                        {
                            bp.Hits = HitRule.Parse(hit);
                            if (bp.Hits == null)
                            {
                                verified.Add(new { verified = false, line = ln,
                                    message = $"'{hit}' is not a hit count. Write a whole number, " +
                                              "or one after >, >=, <, <=, = or % (every Nth hit)" });
                                continue;
                            }
                        }
                        lines[ln] = bp;
                        verified.Add(new { verified = true, line = ln });
                    }
                lock (bps) bps[path] = lines;
                Respond(req, new { breakpoints = verified });
                break;
            }

            case "setExceptionBreakpoints":
            {
                bool errors = false;
                if (args.TryGetProperty("filters", out JsonElement filters))
                    foreach (JsonElement f in filters.EnumerateArray())
                        if (f.GetString() == ErrorsFilter) errors = true;
                breakOnErrors = errors;
                Respond(req, new { breakpoints = Array.Empty<object>() });
                break;
            }

            case "exceptionInfo":
            {
                Exception? e = lastError;
                if (e == null) { Respond(req, success: false, message: "not stopped on an error"); break; }
                Respond(req, new
                {
                    exceptionId = e.GetType().Name,
                    description = e.Message,
                    breakMode = "always",
                });
                break;
            }

            case "pause":
                // The next line the program reaches stops, reason "pause".
                if (paused == null) mode = Mode.Pause;
                Respond(req);
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

            case "setVariable":
                SetVariable(req, args);
                break;

            case "disconnect":
                Respond(req);
                if (attached && ReferenceEquals(Engine.PendingSink, this))
                    Engine.PendingSink = null;
                disconnected = true;
                break;

            default:
                Respond(req, success: false, message: $"unsupported: {cmd}");
                break;
        }
    }

    // ---- running the program ------------------------------------------

    void StartProgram()
    {
        if (entry == null) return;      // attach: the host runs the program
        mode = stopOnEntry ? Mode.Entry : Mode.Run;
        StartOnConsole(entry);
    }

    /// <summary>The program on this process's own console: the keyboard
    /// it reads, the screen it draws on and the error stream it reports
    /// to are the ones `mill run` would give it. Only the exit code and
    /// the end of the session travel as protocol events. Its own thread
    /// with the program stack, as every launcher gives a program: a
    /// thread-pool thread's size cannot be set, and a deep recursion
    /// under the debugger must return exactly as it does under
    /// `mill run`.</summary>
    void StartOnConsole(Func<TextWriter, TextReader, string[], int> run)
    {
        new Thread(() =>
        {
            int rc;
            try
            {
                Engine.PendingSink = this;
                rc = run(Console.Out, Console.In, programArgs);
                AfterRun?.Invoke();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"perch: {e.Message}");
                rc = 1;
            }
            finally
            {
                Engine.PendingSink = null;
                Console.Out.Flush();
            }
            Event("exited", new { exitCode = rc });
            Event("terminated");
        }, Engine.ProgramStackBytes) { IsBackground = true, Name = "shoddy-program" }.Start();
    }

    void Resume(JsonElement req, Mode m)
    {
        if (paused == null) return;
        stepDepth = paused.Frames.Count;
        mode = m;
        paused = null;
        lastError = null;
        varRefs.Clear();
        gate.Release();
    }

    /// <summary>The exception filter's name, as the client sees it in the
    /// Breakpoints view.</summary>
    const string ErrorsFilter = "errors";

    /// <summary>A runtime error, reported before anything unwinds: the
    /// program stops at the failing line, with every frame, binding and
    /// stack value still there to inspect. Continuing lets the error go
    /// on, and the program ends exactly as it would under `mill run`.</summary>
    public void OnError(Engine rt, Exception error)
    {
        if (!breakOnErrors) return;
        paused = rt;
        lastError = error;
        DebugFrame top = rt.Frames[^1];
        stopFrame = top;
        stopFile = top.FileId;
        stopLine = top.Line;
        varRefs.Clear();
        Event("stopped", new
        {
            reason = "exception",
            description = "Runtime error",
            text = error.Message,
            threadId = 1,
            allThreadsStopped = true,
        });
        gate.Wait();
    }

    // Where the program last stopped: the frame (one call, by identity),
    // its file and its line. One source line can re-hook more than once
    // while it runs — a list literal, a lambda or an If branch on the same
    // line opens a block that hooks the line again — and none of those is
    // a new line to stop on. A step, or a breakpoint, stops on the next
    // line, or in another call; never again on the line it is already on.
    DebugFrame? stopFrame;
    int stopFile, stopLine;

    bool AtStop(DebugFrame top) =>
        ReferenceEquals(top, stopFrame) && top.FileId == stopFile && top.Line == stopLine;

    /// <summary>The line hook — runs on the program thread and blocks it
    /// while the front-end inspects state.</summary>
    public void OnLine(Engine rt)
    {
        DebugFrame top = rt.Frames[^1];
        bool same = AtStop(top);
        string? reason = mode switch
        {
            Mode.Entry => "entry",
            Mode.Pause => "pause",
            // Into: the next line anywhere, a called Def's included.
            Mode.StepIn when !same => "step",
            // Over: the next line in this call or a caller. A tail call
            // replaces the call (a new frame at the same depth), and that
            // is the next line too.
            Mode.StepOver when rt.Frames.Count < stepDepth => "step",
            Mode.StepOver when rt.Frames.Count == stepDepth && !same => "step",
            // Out: the first line back in a caller.
            Mode.StepOut when rt.Frames.Count < stepDepth => "step",
            _ => null,
        };
        if (reason == null && !same && CheckBreakpoint(rt, top)) reason = "breakpoint";
        if (reason == null) return;

        paused = rt;
        stopFrame = top;
        stopFile = top.FileId;
        stopLine = top.Line;
        varRefs.Clear();
        Event("stopped", new { reason, threadId = 1, allThreadsStopped = true });
        gate.Wait();
    }

    // The breakpoint location last considered, so one execution of a line
    // is one hit however many times the line re-hooks: a condition is
    // asked once, a hit counted once, a logpoint logged once.
    DebugFrame? hookFrame;
    int hookFile, hookLine;

    Breakpoint? FindBreakpoint(Engine rt, DebugFrame f)
    {
        if (f.FileId >= rt.DebugFiles.Length) return null;
        string path = Path.GetFullPath(rt.DebugFiles[f.FileId]);
        lock (bps)
            return bps.TryGetValue(path, out Dictionary<int, Breakpoint>? lines) &&
                   lines.TryGetValue(f.Line, out Breakpoint? bp) ? bp : null;
    }

    /// <summary>Does a breakpoint on this line stop the program? Its
    /// condition first (only a True one counts as a hit), then its hit
    /// count, then a logpoint, which writes its message and never stops.
    /// A condition that cannot be evaluated stops, and says why: a
    /// breakpoint that silently never fires is the worse failure.</summary>
    bool CheckBreakpoint(Engine rt, DebugFrame top)
    {
        if (ReferenceEquals(top, hookFrame) && top.FileId == hookFile && top.Line == hookLine)
            return false;
        Breakpoint? bp = FindBreakpoint(rt, top);
        if (bp == null) return false;
        hookFrame = top;
        hookFile = top.FileId;
        hookLine = top.Line;

        if (bp.Condition != null)
        {
            try
            {
                Value[] got = EvaluateHere(rt, rt.Frames.Count - 1, bp.Condition);
                if (got.Length != 1 || got[0].T != VType.Bool)
                {
                    Say($"breakpoint condition '{bp.Condition}' must answer True or False; " +
                        $"it answered {Shown(got)}\n");
                    return true;
                }
                if (!got[0].B) return false;
            }
            catch (Exception e)
            {
                Say($"breakpoint condition '{bp.Condition}' failed: {e.Message}\n");
                return true;
            }
        }
        bp.Count++;
        if (bp.Hits != null && !bp.Hits.Allows(bp.Count)) return false;
        if (bp.LogMessage != null)
        {
            Say(Interpolate(rt, bp.LogMessage) + "\n");
            return false;
        }
        return true;
    }

    /// <summary>A logpoint's message with every {expression} replaced by
    /// what it evaluates to. {{ and }} are literal braces.</summary>
    string Interpolate(Engine rt, string message)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < message.Length; i++)
        {
            char c = message[i];
            if (c == '{' && i + 1 < message.Length && message[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < message.Length && message[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (c != '{') { sb.Append(c); continue; }
            int close = message.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(message, i, message.Length - i); break; }
            string expr = message[(i + 1)..close];
            try { sb.Append(Displayed(EvaluateHere(rt, rt.Frames.Count - 1, expr))); }
            catch (Exception e) { sb.Append($"<{expr}: {e.Message}>"); }
            i = close;
        }
        return sb.ToString();
    }

    /// <summary>A string property of a request, or null when it is
    /// absent or blank.</summary>
    static string? NonEmpty(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    /// <summary>Text for the Debug Console, as an output event.</summary>
    void Say(string text) => Event("output", new { category = "console", output = text });

    static string Repr(Value v)
    {
        var sw = new StringWriter();
        Printer.Repr(sw, v);
        return sw.ToString();
    }

    /// <summary>Values as the debugger shows them: their Repr, and "(no
    /// value)" for an expression that left none.</summary>
    static string Shown(Value[] vs) =>
        vs.Length == 0 ? "(no value)" : string.Join("  ", vs.Select(Repr));

    /// <summary>Values as a logpoint writes them: a string as its text,
    /// as Print would, everything else as its Repr.</summary>
    static string Displayed(Value[] vs) =>
        string.Join(" ", vs.Select(v => v.T == VType.Str ? v.Str! : Repr(v)));

    /// <summary>The bindings an expression sees in a frame: the top-level
    /// Lets, then the frame's own, which shadow them.</summary>
    static List<(string Name, Value V)> BindingsIn(Engine rt, int frameIndex)
    {
        var all = new List<(string Name, Value V)>(rt.Globals);
        if (frameIndex >= 0 && frameIndex < rt.Frames.Count)
            all.AddRange(rt.Frames[frameIndex].Binds);
        return all;
    }

    /// <summary>Evaluate an expression in a frame of the stopped program.
    /// A bare name of a binding is answered directly; anything else is
    /// compiled by the <see cref="Evaluator"/>.</summary>
    Value[] EvaluateHere(Engine rt, int frameIndex, string expression)
    {
        string text = expression.Trim();
        List<(string Name, Value V)> bindings = BindingsIn(rt, frameIndex);
        string word = text.ToUpperInvariant();
        for (int k = bindings.Count - 1; k >= 0; k--)
            if (bindings[k].Name == word) return new[] { bindings[k].V };
        if (Evaluator == null)
            throw new ShoddyError(0, $"unknown name: {word}. This debugger is running inside a " +
                "host program, which carries no compiler, so it can show a variable by name " +
                "but cannot evaluate an expression");
        return Evaluator(rt, bindings, text);
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

    // Where a shown value lives, so setting it can write it back: a local
    // of a frame, a top-level Let, a value-stack slot, or an element of a
    // value that itself lives somewhere. Values are immutable, so setting
    // an element builds the containing value anew and sets THAT.
    abstract record Loc;
    sealed record LocalLoc(int Frame, string Name) : Loc;
    sealed record GlobalLoc(string Name) : Loc;
    sealed record StackLoc(int Index) : Loc;
    sealed record ElemLoc(Loc Parent, int Index) : Loc;

    /// <summary>A composite shown in the Variables view: the value, and
    /// where it lives (null for the result of an expression, which lives
    /// nowhere).</summary>
    sealed record Held(Value V, Loc? At);

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
                    vars.Add(Var(name, seen[name], new LocalLoc(frameId, name)));
                break;
            }
            case ("globals", _):
                foreach ((string name, Value v) in rt.Globals)
                    vars.Add(Var(name, v, new GlobalLoc(name)));
                break;
            case ("stack", _):
                for (int k = rt.StackView.Count - 1; k >= 0; k--)
                    vars.Add(Var(k == rt.StackView.Count - 1 ? "top" : $"#{k + 1}", rt.StackView[k], new StackLoc(k)));
                break;
            case Held node:
            {
                Value v = node.V;
                Loc? At(int k) => node.At == null ? null : new ElemLoc(node.At, k);
                switch (v.T)
                {
                    case VType.Rec:
                        for (int k = 0; k < v.Elems!.Length; k++)
                            vars.Add(Var(v.RType!.FDisp[k], v.Elems[k], At(k)));
                        break;
                    case VType.Arr:
                        for (int k = 0; k < v.Elems!.Length; k++)
                            vars.Add(Var($"[{k + 1}]", v.Elems[k], At(k)));
                        break;
                    case VType.Quot:
                    {
                        int k = 0;
                        foreach (QItem it in v.List!.Items())
                        {
                            if (it.Lit != null) vars.Add(Var($"[{k + 1}]", it.Lit, At(k)));
                            else vars.Add(new { name = $"[{k + 1}]", value = it.Disp ?? "?", variablesReference = 0 });
                            k++;
                        }
                        break;
                    }
                }
                break;
            }
        }
        return vars;
    }

    static bool IsComposite(Value v) =>
        v.T is VType.Rec or VType.Arr || (v.T == VType.Quot && v.List!.Length > 0);

    object Var(string name, Value v, Loc? at)
    {
        // Scribbler is deliberately a leaf: an opaque handle with no fields
        // reachable from Shoddy, shown as its repr, e.g. Scribbler(640, 480).
        return new
        {
            name,
            value = Repr(v),
            type = Value.TypeName(v.T),
            variablesReference = IsComposite(v) ? NewRef(new Held(v, at)) : 0,
        };
    }

    void Evaluate(JsonElement req, JsonElement args)
    {
        string expr = args.GetProperty("expression").GetString()!;
        Engine? rt = paused;
        if (rt == null) { Respond(req, success: false, message: "not paused"); return; }
        int frameId = args.TryGetProperty("frameId", out JsonElement fe) ? fe.GetInt32() : rt.Frames.Count - 1;
        Value[] got;
        try { got = EvaluateHere(rt, frameId, expr); }
        catch (Exception e)
        {
            Respond(req, success: false, message: e.Message);
            return;
        }
        int reference = got.Length == 1 && IsComposite(got[0]) ? NewRef(new Held(got[0], null)) : 0;
        Respond(req, new { result = Shown(got), variablesReference = reference });
    }

    /// <summary>Set a variable from the Variables view: a local, a
    /// top-level Let, a value-stack slot, or an element of any of those.
    /// The new value is any expression, evaluated in the variable's own
    /// frame. The program reads it from the next thing it does.</summary>
    void SetVariable(JsonElement req, JsonElement args)
    {
        Engine? rt = paused;
        if (rt == null) { Respond(req, success: false, message: "not paused"); return; }
        int reference = args.GetProperty("variablesReference").GetInt32();
        string name = args.GetProperty("name").GetString()!;
        string text = args.GetProperty("value").GetString()!;
        if (reference < 1 || reference > varRefs.Count)
        {
            Respond(req, success: false, message: "that variable is no longer shown");
            return;
        }
        try
        {
            Loc loc = varRefs[reference - 1] switch
            {
                ("locals", int f) => new LocalLoc(f, name),
                ("globals", _) => new GlobalLoc(name),
                ("stack", _) => new StackLoc(name == "top" ? rt.StackView.Count - 1 : int.Parse(name.TrimStart('#')) - 1),
                Held { At: Loc parent } node => new ElemLoc(parent, ChildIndex(node.V, name)),
                _ => throw new ShoddyError(0, "this value is the result of an expression, not a " +
                                              "variable; set the variable that holds it"),
            };
            Value[] got = EvaluateHere(rt, FrameOf(rt, loc), text);
            if (got.Length != 1)
                throw new ShoddyError(0, $"'{text}' gives {got.Length} values; a variable holds one");
            Assign(rt, loc, got[0]);
            Respond(req, new
            {
                value = Repr(got[0]),
                type = Value.TypeName(got[0].T),
                variablesReference = IsComposite(got[0]) ? NewRef(new Held(got[0], loc)) : 0,
            });
        }
        catch (Exception e)
        {
            Respond(req, success: false, message: e.Message);
        }
    }

    static int FrameOf(Engine rt, Loc loc) => loc switch
    {
        LocalLoc l => l.Frame,
        ElemLoc e => FrameOf(rt, e.Parent),
        _ => rt.Frames.Count - 1,
    };

    /// <summary>Which element of a composite a child's name means: a
    /// record field by its name, a list or array element by [k].</summary>
    static int ChildIndex(Value v, string name)
    {
        if (v.T == VType.Rec)
        {
            int f = v.RType!.FDisp.FindIndex(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
            if (f >= 0) return f;
            throw new ShoddyError(0, $"{v.RType.Name} has no field {name}");
        }
        if (name.Length > 2 && name[0] == '[' && name[^1] == ']' && int.TryParse(name[1..^1], out int k))
            return k - 1;
        throw new ShoddyError(0, $"'{name}' is not an element");
    }

    /// <summary>The value a location holds now.</summary>
    static Value Read(Engine rt, Loc loc)
    {
        switch (loc)
        {
            case LocalLoc l:
            {
                List<(string Name, Value V)> binds = rt.Frames[l.Frame].Binds;
                for (int k = binds.Count - 1; k >= 0; k--)
                    if (binds[k].Name == l.Name) return binds[k].V;
                throw new ShoddyError(0, $"there is no variable {l.Name}");
            }
            case GlobalLoc g:
                for (int k = rt.Globals.Count - 1; k >= 0; k--)
                    if (rt.Globals[k].Name == g.Name) return rt.Globals[k].V;
                throw new ShoddyError(0, $"there is no global {g.Name}");
            case StackLoc s:
                return rt.StackView[s.Index];
            case ElemLoc e:
            {
                Value holder = Read(rt, e.Parent);
                if (holder.T is VType.Rec or VType.Arr) return holder.Elems![e.Index];
                return holder.List!.Items().ElementAt(e.Index).Lit!;
            }
            default:
                throw new ShoddyError(0, "internal: unknown location");
        }
    }

    /// <summary>Write a value to a location. An element is written by
    /// rebuilding the value that holds it, with that one element replaced,
    /// and writing the rebuilt value to the holder's own location.</summary>
    static void Assign(Engine rt, Loc loc, Value v)
    {
        switch (loc)
        {
            case LocalLoc l: rt.DbgSetLocal(l.Frame, l.Name, v); return;
            case GlobalLoc g: rt.DbgSetGlobal(g.Name, v); return;
            case StackLoc s: rt.DbgSetStack(s.Index, v); return;
            case ElemLoc e:
            {
                Value holder = Read(rt, e.Parent);
                Value rebuilt;
                if (holder.T is VType.Rec or VType.Arr)
                {
                    var elems = (Value[])holder.Elems!.Clone();
                    elems[e.Index] = v;
                    rebuilt = holder.T == VType.Rec ? Value.OfRec(holder.RType!, elems) : Value.OfArr(elems);
                }
                else
                {
                    List<QItem> items = holder.List!.Items().ToList();
                    if (items.Any(it => it.Lit == null))
                        throw new ShoddyError(0, "this list holds code as well as values, and code is " +
                                                 "not a variable; set the variable that holds the list");
                    var vals = items.Select(it => it.Lit!).ToArray();
                    vals[e.Index] = v;
                    rebuilt = Value.OfList(Cell.FromValues(vals));
                }
                Assign(rt, e.Parent, rebuilt);
                return;
            }
        }
    }

}
