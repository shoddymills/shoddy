// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text;
using System.Text.Json;

namespace Shoddy.Mcp;

/// <summary>One piece of a tool's answer. Text, or a picture — never a
/// stream and never a handle, because the dictionary answers text and
/// the HOST answers everything else.</summary>
public sealed record Block(string Type, string? Text = null, string? Data = null, string? MimeType = null)
{
    public static Block Say(string text) => new("text", Text: text);
    public static Block Image(byte[] png) =>
        new("image", Data: Convert.ToBase64String(png), MimeType: "image/png");
}

public sealed record ToolResult(IReadOnlyList<Block> Content, bool IsError = false)
{
    public static ToolResult Say(string text) => new(new[] { Block.Say(text) });
}

public sealed record ToolSpec(string Name, string Description, string Schema);

/// <summary>
/// The dictionary as callable tools, and the sessions behind them.
///
/// SESSIONS ARE NAMED AND EXPLICIT. A client names the session on every
/// call; an unnamed call uses `main`. A session is created on first use
/// and holds one ShoddyHost, one RckState, one capture table and one
/// output writer. Two sessions do not interfere; two callers on ONE
/// session serialize, because the engine is a single value stack.
///
/// A REFUSED LINE IS NEVER A PROTOCOL ERROR. IsError is for a malformed
/// REQUEST — a missing argument, an unknown tool — and for nothing a
/// Shoddy line did, because a refusal is the engine working correctly
/// and a client told otherwise will retry instead of reading.
/// </summary>
public sealed class SparkyTools : IDisposable
{
    readonly string root;
    readonly bool allowNet;
    readonly SparkyCanvas canvas = new();
    readonly Dictionary<string, SparkySession> sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly object sync = new();

    public SparkyTools(string root, bool allowNet)
    {
        this.root = root;
        this.allowNet = allowNet;
        canvas.Install();
    }

    public string Root => root;

    SparkySession Session(JsonElement args)
    {
        string name = Str(args, "session") ?? "main";
        lock (sync)
        {
            if (sessions.TryGetValue(name, out SparkySession? s)) return s;
            var made = SparkySession.Open(name, root, allowNet, canvas);
            sessions[name] = made;
            _ = made.Opening();       // sparkyrc, once, on the way in
            return made;
        }
    }

    // ---- what the client is offered ----

    const string SessionArg =
        "\"session\":{\"type\":\"string\",\"description\":\"Which session. Omit for 'main'.\"}";

    public IReadOnlyList<ToolSpec> List() => new[]
    {
        new ToolSpec("eval",
            "Evaluate one or more RPN lines in order and answer what each printed, what it "
          + "said, and what the stack became. This is the main tool: compute answers here "
          + "rather than working them out yourself. A line that is refused comes back as a "
          + "refusal, not an error, and leaves the stack as it was — so trying something is "
          + "cheap. A drawing blitted during the line comes back as a picture in the same "
          + "answer.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"lines\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},"
          + "\"description\":\"RPN lines, submitted in order. A ': NAME ... ;' definition may span several.\"},"
          + SessionArg + "},\"required\":[\"lines\"]}"),

        new ToolSpec("define",
            "Define a word: the whole ': NAME ... ;' at once. Remember that a user word takes "
          + "exactly ONE cell, and that its body may only name words that already exist — a "
          + "word cannot call itself.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"definition\":{\"type\":\"string\",\"description\":\"The whole definition, e.g. ': VAT DUP 0.2 * + ;'\"},"
          + SessionArg + "},\"required\":[\"definition\"]}"),

        new ToolSpec("stack",
            "The stack as it stands, rendered the way the calculator renders it.",
            "{\"type\":\"object\",\"properties\":{" + SessionArg + "}}"),

        new ToolSpec("help",
            "A word's exact stack effect, description, and whether it touches the world — read "
          + "from the running dictionary. Ask this rather than guessing whether a word exists "
          + "or what it takes.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"word\":{\"type\":\"string\"}," + SessionArg + "},\"required\":[\"word\"]}"),

        new ToolSpec("view",
            "Show the body of a word you defined. Built-in words have no program to view.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"word\":{\"type\":\"string\"}," + SessionArg + "},\"required\":[\"word\"]}"),

        new ToolSpec("words",
            "Every word in the dictionary, grouped by the seed it came from — or, with "
          + "by='effect', split into what touches nothing and what touches the world.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"by\":{\"type\":\"string\",\"enum\":[\"seed\",\"effect\"]}," + SessionArg + "}}"),

        new ToolSpec("canvas",
            "The current drawing as a PNG, blitted or not. Use this when a chart or turtle "
          + "drawing was made and you did not see it — forgetting to blit costs nothing here.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"surface\":{\"type\":\"integer\",\"description\":\"1-based, in the order surfaces were opened. Omit for the most recently drawn.\"},"
          + SessionArg + "}}"),

        new ToolSpec("save",
            "Write the words defined in this session to a file under the server's root.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"file\":{\"type\":\"string\"}," + SessionArg + "},\"required\":[\"file\"]}"),

        new ToolSpec("load",
            "Read a file of definitions back in. One bad line refuses the whole file.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"file\":{\"type\":\"string\"}," + SessionArg + "},\"required\":[\"file\"]}"),

        new ToolSpec("tape",
            "The session's tape: every line entered and what it answered. With a file, writes "
          + "it under the server's root instead.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"file\":{\"type\":\"string\"}," + SessionArg + "}}"),

        new ToolSpec("machines",
            "Which subjects this server can teach: the seed groups in the dictionary and the "
          + "pages behind them.",
            "{\"type\":\"object\",\"properties\":{" + SessionArg + "}}"),

        new ToolSpec("subject",
            "A teaching card for one machine: what it is for, worked examples that run at this "
          + "prompt, and the live list of the words it contributes.",
            "{\"type\":\"object\",\"properties\":{"
          + "\"machine\":{\"type\":\"string\",\"description\":\"A machine, seed or WORDS heading — 'alg', 'seedalg' and 'seed-alg' all work.\"},"
          + SessionArg + "},\"required\":[\"machine\"]}"),

        new ToolSpec("reset",
            "Start the session again: empty stack, no history, no tape, and only the words it "
          + "started with. Words loaded from sparkyrc are NOT restored — reset builds a fresh "
          + "dictionary and reloads nothing.",
            "{\"type\":\"object\",\"properties\":{" + SessionArg + "}}"),

        new ToolSpec("abandon",
            "Give up on a session that is stuck — a network fetch against a peer that will "
          + "never reply blocks its engine by design. This starts a NEW engine: sparkyrc is "
          + "reloaded and unsaved words are gone. A turtle drawing is lost with it; a chart is "
          + "one word to redraw.",
            "{\"type\":\"object\",\"properties\":{" + SessionArg + "}}"),
    };

    // ---- the dispatch ----

    public async Task<ToolResult> CallAsync(string name, JsonElement args)
    {
        switch (name.ToLowerInvariant())
        {
            case "eval": return await Eval(Session(args), Lines(args), args);
            case "define":
            {
                string? d = Str(args, "definition");
                if (d is null) return Bad("define needs a definition");
                return await Eval(Session(args), new[] { d }, args);
            }
            case "stack": return ToolResult.Say(Join(Session(args).Stack()));
            case "help":
            {
                string? w = Str(args, "word");
                return w is null ? Bad("help needs a word") : ToolResult.Say(Join(Session(args).Help(w)));
            }
            case "view":
            {
                string? w = Str(args, "word");
                return w is null ? Bad("view needs a word") : ToolResult.Say(Join(Session(args).View(w)));
            }
            case "words":
            {
                SparkySession s = Session(args);
                return ToolResult.Say(Join(
                    string.Equals(Str(args, "by"), "effect", StringComparison.OrdinalIgnoreCase)
                        ? s.WordsByEffect() : s.Words()));
            }
            case "canvas": return Canvas(Session(args), Int(args, "surface"));
            case "save":
            {
                string? f = Str(args, "file");
                return f is null ? Bad("save needs a file name") : ToolResult.Say(Session(args).SaveWords(f));
            }
            case "load":
            {
                string? f = Str(args, "file");
                if (f is null) return Bad("load needs a file name");
                try { return ToolResult.Say(Session(args).LoadWords(f)); }
                // A file that is not there, or a definition that will not
                // load, is an ordinary answer to give — not a protocol
                // fault. Same rule as a refused line.
                catch (Exception e) when (e is FileNotFoundException or InvalidOperationException)
                { return ToolResult.Say("refused: " + e.Message); }
            }
            case "tape":
            {
                SparkySession s = Session(args);
                string? f = Str(args, "file");
                return ToolResult.Say(f is null ? Join(s.Tape()) : s.SaveTape(f));
            }
            case "machines": return ToolResult.Say(Machines(Session(args)));
            case "subject":
            {
                string? m = Str(args, "machine");
                return m is null ? Bad("subject needs a machine") : Subject(Session(args), m);
            }
            case "reset":
                return ToolResult.Say("reset\n" + Join(await Session(args).ResetAsync()));
            case "abandon":
                return ToolResult.Say("abandoned — a new engine, and sparkyrc read again\n"
                                    + Join(Session(args).Abandon()));
            default:
                return Bad("there is no tool called " + name);
        }
    }

    // ---- eval, and the three things it keeps apart ----

    async Task<ToolResult> Eval(SparkySession session, IReadOnlyList<string> lines, JsonElement args)
    {
        if (lines.Count == 0) return Bad("eval needs at least one line");
        IReadOnlyList<SparkyTurn> turns = await session.SubmitAsync(lines);
        if (turns.Count == 0)
            return ToolResult.Say("the session was abandoned while this was running; nothing was committed");

        var sb = new StringBuilder();
        foreach (SparkyTurn t in turns)
        {
            sb.Append("> ").Append(t.Line).Append('\n');
            if (t.Unfinished)
            {
                sb.Append("[unfinished] the line is still open — send the rest of it\n");
                continue;
            }
            foreach (string p in t.Printed) sb.Append("[printed] ").Append(p).Append('\n');
            foreach (string s in t.Said) sb.Append("[said] ").Append(s).Append('\n');
            if (t.Error != null) sb.Append("[refused] ").Append(t.Error).Append('\n');
            foreach (string s in t.Stack) sb.Append("[stack] ").Append(s).Append('\n');
        }

        var content = new List<Block> { Block.Say(sb.ToString().TrimEnd('\n')) };
        // A line that blitted carries its picture back with the numbers
        // behind it — which is the whole reason the blit words are the
        // capture seam rather than a list of drawing words.
        if (session.BlittedLastTurn && session.Canvas(null) is { } frame)
        {
            content.Add(Block.Say("The drawing this line blitted:"));
            content.Add(Block.Image(frame.Png()));
        }
        return new ToolResult(content);
    }

    static ToolResult Canvas(SparkySession session, int? surface)
    {
        SparkyFrame? frame = session.Canvas(surface);
        if (frame is null)
            return ToolResult.Say(session.CanvasCount == 0
                ? "nothing has been drawn in this session — open a surface with PLOTOPEN, "
                + "TURTLEOPEN or SCRIBOPEN first"
                : $"there is no surface {surface}; this session has opened {session.CanvasCount}");
        return new ToolResult(new[]
        {
            Block.Say($"{frame.Width}x{frame.Height}, of {session.CanvasCount} "
                    + (session.CanvasCount == 1 ? "surface" : "surfaces") + " in this session"),
            Block.Image(frame.Png()),
        });
    }

    // ---- teaching ----

    string Machines(SparkySession session)
    {
        var sb = new StringBuilder();
        sb.Append("The groups in this dictionary, as WORDS lists them. Ask `subject` for any "
                + "of them.\n\n");
        foreach (string g in Groups(session)) sb.Append("- ").Append(g).Append('\n');
        return sb.ToString();
    }

    ToolResult Subject(SparkySession session, string machine)
    {
        string? card = SparkyGrounding.Card(machine);
        IReadOnlyList<string> live = GroupWords(session, machine);
        if (card is null && live.Count == 0)
            return ToolResult.Say(
                "there is no subject called " + machine + "; ask `machines` for the list");

        var sb = new StringBuilder();
        sb.Append(card ?? "# " + machine + "\n");
        sb.Append("\n## The words it contributes, read from the running dictionary\n\n");
        sb.Append(live.Count == 0
            ? "(this page's machine contributes no words at this prompt directly — its words "
            + "reach you through a seed, so ask `subject` for the seed instead)\n"
            : string.Join("\n", live) + "\n");
        sb.Append("\nEvery one of those carries its own stack effect and description: ask "
                + "`help` rather than assuming.\n");
        return ToolResult.Say(sb.ToString());
    }

    /// <summary>The group headings in the listing, without their dashes.
    /// Read from RckWords rather than from a list here, so a seed added
    /// to the fold appears without this file being touched.</summary>
    static IEnumerable<string> Groups(SparkySession session) =>
        session.Words()
               .Where(l => l.StartsWith("-- ", StringComparison.Ordinal) && l.EndsWith(" --", StringComparison.Ordinal))
               .Select(l => l[3..^3]);

    /// <summary>The words under one heading. `alg`, `seedalg` and
    /// `seed-alg` all name the same group, because a caller reading a
    /// WORDS heading has only the third spelling.</summary>
    static IReadOnlyList<string> GroupWords(SparkySession session, string machine)
    {
        string want = machine.Trim().ToLowerInvariant().Replace("-", "");
        var taking = false;
        var got = new List<string>();
        foreach (string line in session.Words())
        {
            bool head = line.StartsWith("-- ", StringComparison.Ordinal)
                     && line.EndsWith(" --", StringComparison.Ordinal);
            if (head)
            {
                string g = line[3..^3].Replace("-", "");
                taking = g == want || g == "seed" + want;
                continue;
            }
            if (taking) got.Add(line.Trim());
        }
        return got;
    }

    // ---- reading the arguments a client sent ----

    static IReadOnlyList<string> Lines(JsonElement args)
    {
        if (!args.TryGetProperty("lines", out JsonElement v)) return Array.Empty<string>();
        if (v.ValueKind == JsonValueKind.String) return new[] { v.GetString()! };
        if (v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
    }

    static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int n) ? n : null;

    static string Join(IEnumerable<string> lines) => string.Join("\n", lines);

    /// <summary>A malformed REQUEST — the one thing that is an error.
    /// Nothing a Shoddy line did ever reaches here.</summary>
    static ToolResult Bad(string why) => new(new[] { Block.Say(why) }, IsError: true);

    public void Dispose()
    {
        lock (sync)
        {
            foreach (SparkySession s in sessions.Values) s.Dispose();
            sessions.Clear();
        }
        SparkyCanvas.Uninstall();
    }
}
