// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text;
using System.Text.Json;

namespace Shoddy.Mcp;

/// <summary>
/// The protocol: JSON-RPC 2.0 over stdio, one message per line, written
/// by hand over System.Text.Json.
///
/// WHY BY HAND. The whole of src/ carries three package families —
/// Silk.NET for the runtime's window and audio, Roslyn for the compiler,
/// xunit for the tests. A preview-versioned protocol SDK would become
/// this repository's most volatile dependency and would sit on its least
/// stable layer. The cost of not taking it is the few hundred lines
/// below, covering initialize, tools, resources, prompts and framing.
///
/// TRANSPORT IS LOCAL AND STDIO. A server reachable over a socket is a
/// different threat model and a different document; nothing here opens
/// or listens on one.
///
/// STDOUT IS THE PROTOCOL AND NOTHING ELSE MAY WRITE TO IT. The engine's
/// PRINT goes to the session's own writer, never the console — which is
/// what makes it safe to hand a client a dictionary containing PRINT at
/// all. Diagnostics go to stderr.
/// </summary>
public sealed class SparkyServer
{
    /// <summary>Versions this server knows how to speak. A client asking
    /// for one of them gets it back; a client asking for anything else
    /// is answered with the newest we have, which is what the protocol
    /// says to do rather than refusing outright.</summary>
    static readonly string[] Protocols = { "2025-06-18", "2025-03-26", "2024-11-05" };

    readonly SparkyTools tools;
    readonly TextReader input;
    readonly TextWriter output;

    public SparkyServer(SparkyTools tools, TextReader input, TextWriter output)
    {
        this.tools = tools;
        this.input = input;
        this.output = output;
    }

    /// <summary>Read a line, answer it, repeat until stdin closes. One
    /// message at a time: the engine is serialized anyway, and a server
    /// whose caller is one client has nothing to gain from
    /// interleaving.</summary>
    public async Task RunAsync(CancellationToken cancel = default)
    {
        while (!cancel.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(cancel).ConfigureAwait(false);
            if (line is null) return;                 // stdin closed: that is the way out
            if (line.Trim().Length == 0) continue;

            string? reply = await AnswerAsync(line).ConfigureAwait(false);
            if (reply is null) continue;              // a notification earns no answer
            await output.WriteLineAsync(reply).ConfigureAwait(false);
            await output.FlushAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>One message in, at most one message out. Public so the
    /// tests can drive the protocol without a process.</summary>
    public async Task<string?> AnswerAsync(string message)
    {
        JsonElement req;
        try { req = JsonDocument.Parse(message).RootElement; }
        catch (JsonException e) { return Error(null, -32700, "the message is not JSON: " + e.Message); }

        JsonElement? id = req.TryGetProperty("id", out JsonElement idv) ? idv : null;
        string method = req.TryGetProperty("method", out JsonElement m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : "";
        JsonElement args = req.TryGetProperty("params", out JsonElement p) ? p : default;

        // A notification has no id, and the protocol forbids answering
        // one. notifications/initialized and notifications/cancelled are
        // the two a client actually sends.
        if (id is null)
            return null;

        try
        {
            switch (method)
            {
                case "initialize": return Ok(id, Initialize(args));
                case "ping": return Ok(id, w => { w.WriteStartObject(); w.WriteEndObject(); });
                case "tools/list": return Ok(id, ToolsList);
                case "tools/call": return Ok(id, await ToolsCall(args).ConfigureAwait(false));
                case "resources/list": return Ok(id, ResourcesList);
                case "resources/read": return Ok(id, ResourcesRead(args));
                case "resources/templates/list": return Ok(id, ResourceTemplates);
                case "prompts/list": return Ok(id, PromptsList);
                case "prompts/get": return Ok(id, PromptsGet(args));
                default: return Error(id, -32601, "no method called " + method);
            }
        }
        catch (ArgumentException e) { return Error(id, -32602, e.Message); }
        catch (Exception e) { return Error(id, -32603, e.Message); }
    }

    // ---- initialize ----

    Action<Utf8JsonWriter> Initialize(JsonElement args)
    {
        string asked = args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("protocolVersion", out JsonElement v)
                    && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        string speak = Protocols.Contains(asked) ? asked : Protocols[0];
        return w =>
        {
            w.WriteStartObject();
            w.WriteString("protocolVersion", speak);
            w.WriteStartObject("capabilities");
            w.WriteStartObject("tools"); w.WriteEndObject();
            w.WriteStartObject("resources"); w.WriteEndObject();
            w.WriteStartObject("prompts"); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("serverInfo");
            w.WriteString("name", "sparky");
            w.WriteString("title", "Sparky — the Shoddy reckoner");
            w.WriteString("version", Version);
            w.WriteEndObject();
            // Named after Karen Spärck Jones, whose subject was
            // retrieving the right material for a question — which is
            // half of what this server does.
            w.WriteString("instructions",
                "A programmable RPN calculator with the whole Shoddy standard library at it: "
              + "statistics, matrices, linear algebra, LP and MIP, finance, symbolic algebra, "
              + "neural nets, regular expressions, indexed files, number bases, charts and "
              + "turtle graphics. COMPUTE answers with `eval` rather than working them out "
              + "yourself, and show the working. Read sparky://grounding/reckoner before your "
              + "first line — two of its rules (a user word takes exactly one cell, and a word "
              + "cannot call itself) are not what a concatenative language usually does. Ask "
              + "`help` rather than assuming a word exists; every word carries its own stack "
              + "effect and description.");
            w.WriteEndObject();
        };
    }

    static string Version =>
        typeof(SparkyServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- tools ----

    void ToolsList(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");
        foreach (ToolSpec t in tools.List())
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteString("description", t.Description);
            w.WritePropertyName("inputSchema");
            w.WriteRawValue(t.Schema);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    async Task<Action<Utf8JsonWriter>> ToolsCall(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
         || !args.TryGetProperty("name", out JsonElement n)
         || n.ValueKind != JsonValueKind.String)
            throw new ArgumentException("tools/call needs a tool name");

        JsonElement inner = args.TryGetProperty("arguments", out JsonElement a) ? a : default;
        ToolResult r = await tools.CallAsync(n.GetString()!, inner).ConfigureAwait(false);
        return w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("content");
            foreach (Block b in r.Content)
            {
                w.WriteStartObject();
                w.WriteString("type", b.Type);
                if (b.Text != null) w.WriteString("text", b.Text);
                if (b.Data != null) w.WriteString("data", b.Data);
                if (b.MimeType != null) w.WriteString("mimeType", b.MimeType);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("isError", r.IsError);
            w.WriteEndObject();
        };
    }

    // ---- resources ----

    void ResourcesList(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("resources");
        Resource(w, SparkyGrounding.ShapeUri, "The shape of Shoddy",
            "What does not change about the language: no mutation, no For, values not "
          + "exceptions, 1-based, booleans are not numbers.");
        Resource(w, SparkyGrounding.ReckonerUri, "How a reckoner line works",
            "RPN lines, lists and programs, ': NAME ... ;', the line-is-a-transaction rule, "
          + "and the two facts every concatenative language gets wrong here.");
        Resource(w, SparkyGrounding.SurfaceUri, "What this server is, and is not",
            "What Sparky exposes, what it deliberately does not, and the four things that "
          + "catch a text-only caller.");
        Resource(w, SparkyGrounding.DictionaryUri, "The dictionary",
            "Every word with its stack effect and description, generated live from the "
          + "running session.");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    void ResourceTemplates(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("resourceTemplates");
        w.WriteStartObject();
        w.WriteString("uriTemplate", SparkyGrounding.SubjectPrefix + "{machine}");
        w.WriteString("name", "A subject card");
        w.WriteString("description",
            "What one machine is for, worked examples that run at this prompt, and the live "
          + "list of words it contributes.");
        w.WriteString("mimeType", "text/markdown");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void Resource(Utf8JsonWriter w, string uri, string name, string description)
    {
        w.WriteStartObject();
        w.WriteString("uri", uri);
        w.WriteString("name", name);
        w.WriteString("description", description);
        w.WriteString("mimeType", "text/markdown");
        w.WriteEndObject();
    }

    Action<Utf8JsonWriter> ResourcesRead(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
         || !args.TryGetProperty("uri", out JsonElement u)
         || u.ValueKind != JsonValueKind.String)
            throw new ArgumentException("resources/read needs a uri");
        string uri = u.GetString()!;

        string text =
            uri == SparkyGrounding.ShapeUri ? SparkyGrounding.Shape
          : uri == SparkyGrounding.ReckonerUri ? SparkyGrounding.Reckoner
          : uri == SparkyGrounding.SurfaceUri ? SparkyGrounding.Surface
          : uri == SparkyGrounding.DictionaryUri ? Dictionary()
          : uri.StartsWith(SparkyGrounding.SubjectPrefix, StringComparison.Ordinal)
              ? Subject(uri[SparkyGrounding.SubjectPrefix.Length..])
          : throw new ArgumentException("there is no resource at " + uri);

        return w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteString("uri", uri);
            w.WriteString("mimeType", "text/markdown");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        };
    }

    /// <summary>Generated live, never authored: every word, its effect,
    /// its description and whether it touches the world, read from the
    /// state the server is actually running.</summary>
    string Dictionary()
    {
        ToolResult words = tools.CallAsync("words", default).GetAwaiter().GetResult();
        return "# The dictionary\n\nEvery word, grouped by the seed it came from. Ask `help` "
             + "for any of them — the effect line and the description come from the same "
             + "place this list does.\n\n```\n"
             + string.Join("\n", words.Content.Select(b => b.Text)) + "\n```\n";
    }

    string Subject(string machine)
    {
        string json = "{\"machine\":\"" + JsonEncodedText.Encode(machine) + "\"}";
        ToolResult r = tools.CallAsync("subject", JsonDocument.Parse(json).RootElement)
                            .GetAwaiter().GetResult();
        return string.Join("\n", r.Content.Select(b => b.Text));
    }

    // ---- prompts ----

    void PromptsList(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("prompts");

        w.WriteStartObject();
        w.WriteString("name", "teach");
        w.WriteString("description",
            "A lesson in one machine's subject area, grounded in its own words and in worked "
          + "examples that run at this prompt.");
        w.WriteStartArray("arguments");
        Argument(w, "machine", "Which subject — ask the `machines` tool for the list.", true);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartObject();
        w.WriteString("name", "check-my-working");
        w.WriteString("description",
            "The student states a problem and their answer; the model recomputes it in the "
          + "dictionary and compares, showing the lines it used.");
        w.WriteStartArray("arguments");
        Argument(w, "subject", "What the problem is about.", true);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void Argument(Utf8JsonWriter w, string name, string description, bool required)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("description", description);
        w.WriteBoolean("required", required);
        w.WriteEndObject();
    }

    Action<Utf8JsonWriter> PromptsGet(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
         || !args.TryGetProperty("name", out JsonElement n)
         || n.ValueKind != JsonValueKind.String)
            throw new ArgumentException("prompts/get needs a name");
        string name = n.GetString()!;
        JsonElement inner = args.TryGetProperty("arguments", out JsonElement a) ? a : default;
        string of = inner.ValueKind == JsonValueKind.Object
                 && inner.TryGetProperty(name == "teach" ? "machine" : "subject", out JsonElement s)
                 && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";

        (string about, string text) = name switch
        {
            "teach" => ($"A lesson in {of}",
                "Teach me " + of + ", using this server to do it.\n\n"
              + "Read sparky://grounding/reckoner first if you have not this session, then call "
              + "the `subject` tool for " + of + ". Build the lesson from what it answers: use "
              + "the machine's own words, and RUN every example with `eval` rather than "
              + "asserting what it would print. Show me the lines and the answers. If a chart "
              + "would make a point better than a number, draw one and blit it. Ask `help` for "
              + "any word before you use it — never invent one."),
            "check-my-working" => ($"Checking working in {of}",
                "I have a problem in " + of + " and an answer I am not sure of. Ask me for "
              + "both.\n\nThen RECOMPUTE it in this server with `eval` — do not check it in "
              + "your head — and show me the lines you used. If we disagree, find where: work "
              + "through it a step at a time on the stack so I can see which step I got wrong. "
              + "If we agree, say so and show the line that proves it."),
            _ => throw new ArgumentException("there is no prompt called " + name),
        };

        return w =>
        {
            w.WriteStartObject();
            w.WriteString("description", about);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartObject("content");
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        };
    }

    // ---- framing ----

    static string Ok(JsonElement? id, Action<Utf8JsonWriter> result) =>
        Envelope(id, w => { w.WritePropertyName("result"); result(w); });

    static string Error(JsonElement? id, int code, string message) =>
        Envelope(id, w =>
        {
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
        });

    /// <summary>One JSON-RPC message, on one line. Indented output would
    /// be a protocol violation on a newline-delimited transport, which
    /// is why nothing here is written with indentation.</summary>
    static string Envelope(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is null) w.WriteNullValue(); else id.Value.WriteTo(w);
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }
}
