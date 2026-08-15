using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;

namespace Fettler.Mcp;

/// <summary>
/// The tools a model sees, and the one place they are turned into work.
///
/// <para><b>R3.4 requires that the two front ends share every operation,
/// and this is how that is kept true rather than promised.</b> A tool
/// call is marshalled into the same argument vector the command line
/// takes and handed to the same dispatcher, in machine-readable mode.
/// The MCP answer is therefore byte-identical to what
/// <c>fettle VERB --json</c> prints, which is exactly what R3.6 asks for
/// - and a capability available to a model and not to a script becomes
/// impossible to write rather than merely forbidden.</para>
///
/// <para>The tool names are the CLI verbs, unchanged, because two
/// vocabularies for one surface would break parity on the first day.</para>
/// </summary>
public static class ToolCatalogue
{
    sealed record Tool(string Name, string Summary, string Schema);

    static readonly Tool[] Tools =
    [
        new("find", "Paths matching a glob, with size and modified time. Ordered by path, or by recency with sort=mtime.", """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"glob; * stays within a segment, ** crosses segments, matching is case-insensitive"},
              "in":{"type":"string","description":"which root to search when more than one is open"},
              "sort":{"type":"string","enum":["path","mtime"]},
              "since":{"type":"string","description":"ISO-8601; only files modified after it"},
              "limit":{"type":"integer"},
              "include_generated":{"type":"boolean","description":"do not skip bin, obj, .git, artifacts, node_modules"},
              "exclude":{"type":"array","items":{"type":"string"}}
            }}
            """),

        new("search", "Matching lines as records: path, line, column, text, optional context. Takes several patterns at once.", """
            {"type":"object","required":["patterns"],"properties":{
              "patterns":{"type":"array","items":{"type":"string"},"description":".NET regular expressions unless literal is true"},
              "glob":{"type":"string","description":"restrict to a subset of files"},
              "in":{"type":"string"},
              "literal":{"type":"boolean"},
              "case_sensitive":{"type":"boolean"},
              "context":{"type":"integer"},
              "limit":{"type":"integer"},
              "count":{"type":"boolean","description":"how many, without the hits"},
              "files_only":{"type":"boolean"},
              "include_generated":{"type":"boolean"}
            }}
            """),

        new("read", "Content, encoding, line ending, trailing-newline state and a content hash. Takes several paths in one call.", """
            {"type":"object","required":["paths"],"properties":{
              "paths":{"type":"array","items":{"type":"string"}},
              "from":{"type":"integer"},
              "to":{"type":"integer"}
            }}
            """),

        new("write", "Create or replace a file, keeping its encoding and line endings unless told otherwise.", """
            {"type":"object","required":["path","text"],"properties":{
              "path":{"type":"string"},
              "text":{"type":"string"},
              "overwrite":{"type":"boolean","description":"required to replace an existing file"},
              "encoding":{"type":"string"},
              "eol":{"type":"string","enum":["lf","crlf","cr"]}
            }}
            """),

        new("edit", "A batch of edits, resolved against the files as read and applied all or not at all. May span files.", """
            {"type":"object","required":["script"],"properties":{
              "path":{"type":"string","description":"the file, when the script does not carry a files array"},
              "expect":{"type":"string","description":"the hash read returned; the edit is refused if the file has moved"},
              "dry_run":{"type":"boolean"},
              "script":{"type":"object","description":"either {edits:[...]} or {files:[{path,expect,edits:[...]}]}; an edit is {replace,with,from,to,all} or {insertAfter,text} or {deleteFrom,deleteTo}"}
            }}
            """),

        new("replace", "One substitution everywhere it appears across a glob, all or nothing. Dry run first.", """
            {"type":"object","required":["find","with"],"properties":{
              "find":{"type":"string"},
              "with":{"type":"string"},
              "glob":{"type":"string"},
              "in":{"type":"string"},
              "dry_run":{"type":"boolean"},
              "include_generated":{"type":"boolean"}
            }}
            """),

        new("new", "An empty file, refusing rather than truncating one already there.", """
            {"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}
            """),

        new("mkdir", "A directory and the parents it needs.", """
            {"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}
            """),

        new("move", "Rename or relocate a file or a directory, reporting whether it was atomic.", """
            {"type":"object","required":["from","to"],"properties":{
              "from":{"type":"string"},"to":{"type":"string"},"overwrite":{"type":"boolean"}
            }}
            """),

        new("copy", "Copy a file, or a tree with recursive. Carries the executable bit; does not follow links.", """
            {"type":"object","required":["from","to"],"properties":{
              "from":{"type":"string"},"to":{"type":"string"},
              "recursive":{"type":"boolean"},"overwrite":{"type":"boolean"},"preserve_times":{"type":"boolean"}
            }}
            """),

        new("delete", "Delete one named path. Never a pattern; a non-empty directory needs recursive.", """
            {"type":"object","required":["path"],"properties":{
              "path":{"type":"string"},"recursive":{"type":"boolean"},
              "force":{"type":"boolean","description":"clear a read-only attribute first"}
            }}
            """),

        new("exec", "Set or clear the executable bit. Reports unsupported on Windows rather than pretending.", """
            {"type":"object","required":["path","executable"],"properties":{
              "path":{"type":"string"},"executable":{"type":"boolean"}
            }}
            """),

        new("tasks", "The declared tasks and what each runs.", """
            {"type":"object","properties":{}}
            """),

        new("run", "Run a declared task, capturing its streams and exit code. Never composes a shell command.", """
            {"type":"object","required":["name"],"properties":{
              "name":{"type":"string"},"timeout":{"type":"integer","description":"seconds; 0 means no timeout"}
            }}
            """),

        new("batch", "Several operations in one call, in order, stopping at the first failure. Not transactional.", """
            {"type":"object","required":["script"],"properties":{
              "script":{"type":"object","description":"{operations:[{op,path,...}]}; op is mkdir, new, write, move, copy, delete or exec"}
            }}
            """),
    ];

    public static void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");

        foreach (Tool tool in Tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Summary);
            w.WritePropertyName("inputSchema");
            using JsonDocument schema = JsonDocument.Parse(tool.Schema);
            schema.RootElement.WriteTo(w);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static async Task<Result<string>> InvokeAsync(
        Bench bench, string name, JsonElement arguments, CancellationToken cancel)
    {
        bool known = false;
        foreach (Tool tool in Tools) if (tool.Name == name) { known = true; break; }
        if (!known)
            return Result<string>.Fail(Outcome.Invalid, $"no tool called '{name}'");

        var argv = new List<string> { name, "--json" };
        string stdin = string.Empty;

        void Flag(string flag, string key)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return;
            if (!arguments.TryGetProperty(key, out JsonElement v)) return;

            switch (v.ValueKind)
            {
                case JsonValueKind.True: argv.Add($"--{flag}"); break;
                case JsonValueKind.False: break;
                case JsonValueKind.String: argv.Add($"--{flag}"); argv.Add(v.GetString()!); break;
                case JsonValueKind.Number: argv.Add($"--{flag}"); argv.Add(v.ToString()); break;
            }
        }

        void Positional(string key)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.String)
                argv.Add(v.GetString()!);
        }

        void Each(string key, string flag)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return;
            if (!arguments.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) { argv.Add(flag); argv.Add(item.GetString()!); }
        }

        void Inline(string key)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.Object)
            {
                argv.Add("--script-inline");
                argv.Add(v.GetRawText());
            }
        }

        switch (name)
        {
            case "find":
                Positional("pattern");
                Flag("in", "in"); Flag("sort", "sort"); Flag("since", "since"); Flag("limit", "limit");
                Flag("include-generated", "include_generated"); Each("exclude", "--exclude");
                break;

            case "search":
                Each("patterns", "-e");
                Flag("glob", "glob"); Flag("in", "in"); Flag("literal", "literal");
                Flag("case-sensitive", "case_sensitive"); Flag("context", "context"); Flag("limit", "limit");
                Flag("count", "count"); Flag("files-only", "files_only");
                Flag("include-generated", "include_generated");
                break;

            case "read":
                if (arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("paths", out JsonElement paths)
                    && paths.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement path in paths.EnumerateArray())
                        if (path.ValueKind == JsonValueKind.String) argv.Add(path.GetString()!);
                Flag("from", "from"); Flag("to", "to");
                break;

            case "write":
                Positional("path");
                argv.Add("--stdin");
                Flag("overwrite", "overwrite"); Flag("encoding", "encoding"); Flag("eol", "eol");
                if (arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("text", out JsonElement text)
                    && text.ValueKind == JsonValueKind.String)
                    stdin = text.GetString()!;
                break;

            case "edit":
                Positional("path");
                Flag("expect", "expect"); Flag("dry-run", "dry_run"); Inline("script");
                break;

            case "replace":
                Positional("find"); Positional("with");
                Flag("glob", "glob"); Flag("in", "in"); Flag("dry-run", "dry_run");
                Flag("include-generated", "include_generated");
                break;

            case "new":
            case "mkdir":
                Positional("path");
                break;

            case "move":
                Positional("from"); Positional("to"); Flag("overwrite", "overwrite");
                break;

            case "copy":
                Positional("from"); Positional("to");
                Flag("recursive", "recursive"); Flag("overwrite", "overwrite");
                Flag("preserve-times", "preserve_times");
                break;

            case "delete":
                Positional("path"); Flag("recursive", "recursive"); Flag("force", "force");
                break;

            case "exec":
                Positional("path");
                argv.Add(arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("executable", out JsonElement on)
                    && on.ValueKind == JsonValueKind.True ? "--on" : "--off");
                break;

            case "run":
                Positional("name"); Flag("timeout", "timeout");
                break;

            case "batch":
                Inline("script");
                break;
        }

        CliResult result = await Command
            .RunAsync(argv, new StringReader(stdin), bench, cancel).ConfigureAwait(false);

        // R3.7 already put the complete machine-readable answer, failures
        // included, on stdout - so the tool result is that text verbatim
        // whichever way it went. The caller reads "ok" in the payload;
        // the server marks isError from the exit code.
        return result.ExitCode == ExitCodes.Ok
            ? Result<string>.Ok(result.Stdout.TrimEnd())
            : Result<string>.Fail(new Failure(Outcome.Refused, result.Stdout.TrimEnd()));
    }
}
