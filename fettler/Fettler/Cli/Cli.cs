using System.Text;
using System.Text.Json;
using Fettler.Core;

namespace Fettler.Cli;

/// <summary>What a command produced, without a console anywhere near it
/// so the tests can drive every verb with no process.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The command-line front end of R3.3, which is what lets a `.ps1` and a
/// `.sh` become three-line launchers instead of two copies of the same
/// logic.
///
/// <para><b>R3.7 is the clause that shapes this file.</b> In
/// machine-readable mode the complete result - failures included - is
/// written to stdout, and stderr carries human diagnostics only. That is
/// not a preference: Windows PowerShell 5.1 turns a native program's
/// redirected stderr into <c>NativeCommandError</c> records and sets
/// <c>$?</c> false even on exit code 0, so any design that needs
/// <c>2&gt;&amp;1</c> to retrieve an answer is broken on the primary
/// development platform.</para>
/// </summary>
public static class Command
{
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args, json);
        if (early is not null) return early;

        Result<Roots> roots = args.DeclaredRoots();
        if (!roots.IsOk) return Failed(roots.Failure!, json);

        using var bench = new Bench(roots.Value);
        return await Run(bench, args, json, stdin, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The same command against a boundary that is already open. The MCP
    /// front end uses this, which is what makes R3.4's parity a property
    /// of the design: both front ends reach the operations through one
    /// dispatcher and one renderer, so a capability reachable from a
    /// script and not from a model is not merely forbidden but
    /// unwritable.
    /// </summary>
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, Bench bench, CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args, json);
        if (early is not null) return early;

        return await Run(bench, args, json, stdin, cancel).ConfigureAwait(false);
    }

    static CliResult? Trivial(Arguments args, bool json)
    {
        // Version before help, and not the other way round: `--version`
        // parses as a flag and leaves no verb behind, so an empty-verb
        // test placed first swallows it and prints the help instead.
        if (args.Verb is "version" || args.Has("version"))
            return new CliResult(ExitCodes.Ok, "fettle " + Version + Environment.NewLine, string.Empty);

        if (args.Verb.Length == 0 || args.Verb is "help" or "--help" or "-h" || args.Has("help"))
            return new CliResult(ExitCodes.Ok, Help(), string.Empty);

        return null;
    }

    static async Task<CliResult> Run(
        Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        // R3.14: a flag no verb knows is refused here, before anything
        // runs, because ignoring one does not produce a failure - it
        // produces a WRONG ANSWER. A flag taking a value eats the
        // argument after it, so a misspelt `--gob "*.cs"` swallows the
        // pattern, and the command then searches the whole tree for
        // something else and reports it with complete confidence.
        IReadOnlyList<string> unknown = args.UnknownFlags;
        if (unknown.Count > 0)
        {
            var named = new List<string>();
            foreach (string flag in unknown) named.Add("--" + flag);
            return Failed(new Failure(Outcome.Invalid,
                $"no such flag: {string.Join(", ", named)} - try: fettle help"), json);
        }

        try
        {
            return await Dispatch(bench, args, json, stdin, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failed(new Failure(Outcome.Refused, "the command was cancelled"), json);
        }
    }

    public const string Version = "1.0.0";

    static async Task<CliResult> Dispatch(
        Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel) =>
        args.Verb switch
        {
            "find" => Find(bench, args, json),
            "search" => Search(bench, args, json, stdin),
            "read" => Read(bench, args, json),
            "write" => await Write(bench, args, json, stdin, cancel).ConfigureAwait(false),
            "edit" => await Edit(bench, args, json, stdin, cancel).ConfigureAwait(false),
            "replace" => await Replace(bench, args, json, cancel).ConfigureAwait(false),
            "new" => await Simple(bench.NewFileAsync(args.At(0) ?? "", cancel), json,
                        r => $"created {r.Path.Display}").ConfigureAwait(false),
            "mkdir" => await Simple(bench.MakeDirectoryAsync(args.At(0) ?? "", cancel), json,
                        r => r.Created ? $"created {r.Path.Display}" : $"{r.Path.Display} was already there").ConfigureAwait(false),
            "move" => await Move(bench, args, json, cancel).ConfigureAwait(false),
            "copy" => await Copy(bench, args, json, cancel).ConfigureAwait(false),
            "delete" => await Delete(bench, args, json, cancel).ConfigureAwait(false),
            "exec" => await Exec(bench, args, json, cancel).ConfigureAwait(false),
            "tasks" => TaskList(bench, json),
            "roots" => RootList(bench, json),
            "run" => await Run(bench, args, json, cancel).ConfigureAwait(false),
            "batch" => await Batch(bench, args, json, cancel).ConfigureAwait(false),
            _ => Failed(new Failure(Outcome.Invalid, $"no verb called '{args.Verb}'; try: fettle help"), json),
        };

    // ---- read-only verbs ----

    static CliResult Find(Bench bench, Arguments args, bool json)
    {
        DateTimeOffset? since = null;
        if (args.Value("since") is { } s)
        {
            if (!DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                return Failed(new Failure(Outcome.Invalid, $"--since is not a time this reads: {s}"), json);
            since = parsed;
        }

        var request = new FindRequest(
            Root: args.Value("in"),
            Pattern: args.At(0),
            IncludeGenerated: args.Has("include-generated"),
            Exclude: args.Values("exclude"),
            Since: since,
            Limit: args.Int("limit", 1000),
            ByModified: args.Value("sort") == "mtime" || args.Has("sort-modified"));

        Result<Bounded<FoundFile>> found = bench.FindChecked(request);
        if (!found.IsOk) return Failed(found.Failure!, json);

        Bounded<FoundFile> answer = found.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("files");
                foreach (FoundFile f in answer.Items)
                {
                    w.WriteStartObject();
                    w.WriteString("path", Show(bench, f.Path));
                    w.WriteNumber("bytes", f.Bytes);
                    w.WriteString("modified", f.Modified.UtcDateTime.ToString("o"));
                    WriteFacts(w, f.Facts);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", answer.Count);
                w.WriteBoolean("truncated", answer.Truncated);
                w.WriteNumber("excluded", answer.Excluded);
            }));

        var text = new StringBuilder();
        foreach (FoundFile f in answer.Items)
            text.Append(Show(bench, f.Path).PadRight(48))
                .Append(f.Bytes.ToString().PadLeft(9))
                .Append("  ")
                .AppendLine(f.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        text.Append(answer.Count).Append(answer.Count == 1 ? " file" : " files");
        if (answer.Excluded > 0) text.Append($" ({answer.Excluded} excluded: bin, obj, .git - pass --include-generated)");
        if (answer.Truncated) text.Append(" (limit reached; more files exist)");

        return Ok(text.AppendLine().ToString());
    }

    /// <summary>One pattern per line, blank lines dropped and nothing
    /// trimmed - a trailing space can be part of a pattern. The shape is
    /// the fettle-tasks format's, deliberately: a second rule for
    /// splitting a file into strings is a second rule to get wrong.</summary>
    static void AddLines(List<string> into, string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            if (line.Length > 0) into.Add(line);
    }

    static CliResult Search(Bench bench, Arguments args, bool json, TextReader stdin)
    {
        var patterns = new List<string>(args.Values("pattern"));
        if (args.At(0) is { } first) patterns.Insert(0, first);
        for (int i = 1; i < args.Positional.Count; i++) patterns.Add(args.Positional[i]);

        // R4.20: the pattern is the argument a shell is likeliest to
        // mangle before Fettler ever sees it - quotes, backslashes and a
        // leading -- all mean something to somebody first - so it may
        // arrive as bytes instead. This is R5.11's argument about edit
        // text, applied where it turns out to matter just as much: an
        // eaten quote does not fail, it silently searches for something
        // else.
        if (args.Value("pattern-file") is { } file)
        {
            try { AddLines(patterns, File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(new Failure(Outcome.NotFound,
                    $"cannot read --pattern-file: {e.Message}", file), json);
            }
        }

        if (args.Has("pattern-stdin")) AddLines(patterns, TextIo.WithoutMark(stdin.ReadToEnd()));

        if (patterns.Count == 0)
            return Failed(new Failure(Outcome.Invalid,
                "search needs a pattern: one written plainly, -e PATTERN, "
                + "--pattern-file PATH or --pattern-stdin"), json);

        Result<SearchAnswer> result = bench.Search(new SearchRequest(
            patterns,
            Root: args.Value("in"),
            Glob: args.Value("glob"),
            Kind: args.Has("literal") ? PatternKind.Literal : PatternKind.Regex,
            CaseSensitive: args.Has("case-sensitive"),
            Context: args.Int("context", 0),
            Limit: args.Int("limit", 200),
            IncludeGenerated: args.Has("include-generated"),
            Exclude: args.Values("exclude")));

        if (!result.IsOk) return Failed(result.Failure!, json);
        SearchAnswer answer = result.Value;

        bool countOnly = args.Has("count");
        bool filesOnly = args.Has("files-only");

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("kind", answer.Kind == PatternKind.Literal ? "literal" : "regex");
                w.WriteStartArray("patterns");
                foreach (string p in answer.Patterns) w.WriteStringValue(p);
                w.WriteEndArray();

                if (!countOnly)
                {
                    w.WriteStartArray("hits");
                    foreach (Hit h in answer.Hits)
                    {
                        w.WriteStartObject();
                        w.WriteString("path", Show(bench, h.Path));
                        w.WriteNumber("line", h.Line);
                        w.WriteNumber("column", h.Column);
                        if (!filesOnly) w.WriteString("text", h.Text);
                        if (h.Context.Count > 0 && !filesOnly)
                        {
                            w.WriteStartArray("context");
                            foreach (ContextLine c in h.Context)
                            {
                                w.WriteStartObject();
                                w.WriteNumber("line", c.Number);
                                w.WriteString("text", c.Text);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteNumber("hits_count", answer.Hits.Count);
                w.WriteNumber("files", answer.Files);
                w.WriteNumber("files_searched", answer.FilesSearched);
                w.WriteBoolean("truncated", answer.Truncated);
                w.WriteNumber("excluded", answer.Excluded);
            }));

        var text = new StringBuilder();

        if (countOnly)
        {
            text.Append(answer.Hits.Count).Append(answer.Hits.Count == 1 ? " hit in " : " hits in ")
                .Append(answer.Files).AppendLine(answer.Files == 1 ? " file" : " files");
        }
        else if (filesOnly)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Hit h in answer.Hits)
                if (seen.Add(h.Path.Full)) text.AppendLine(Show(bench, h.Path));
            text.Append(seen.Count).AppendLine(seen.Count == 1 ? " file" : " files");
        }
        else
        {
            foreach (Hit h in answer.Hits)
            {
                text.Append(Show(bench, h.Path)).Append(':').Append(h.Line).Append(':').AppendLine(h.Column.ToString());
                if (h.Context.Count > 0)
                    foreach (ContextLine c in h.Context)
                        text.Append(c.Number.ToString().PadLeft(5)).Append(" | ").AppendLine(c.Text);
                else
                    text.Append(h.Line.ToString().PadLeft(5)).Append(" | ").AppendLine(h.Text);
            }

            text.Append(answer.Hits.Count).Append(answer.Hits.Count == 1 ? " hit in " : " hits in ")
                .Append(answer.Files).Append(answer.Files == 1 ? " file" : " files");
            if (answer.Truncated) text.Append(" (limit reached; more matches exist)");
            text.AppendLine();
        }

        return Ok(text.ToString());
    }

    static CliResult Read(Bench bench, Arguments args, bool json)
    {
        var paths = new List<string>(args.Positional);
        if (paths.Count == 0)
            return Failed(new Failure(Outcome.Invalid, "read needs at least one path"), json);

        int? from = args.Has("from") ? args.Int("from", 1) : null;
        int? to = args.Has("to") ? args.Int("to", int.MaxValue) : null;

        Result<IReadOnlyList<ReadSlice>> result = bench.Read(new ReadRequest(paths, from, to));
        if (!result.IsOk) return Failed(result.Failure!, json);

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("files");
                foreach (ReadSlice s in result.Value)
                {
                    w.WriteStartObject();
                    w.WriteString("path", Show(bench, s.Path));
                    w.WriteString("text", s.Text);
                    w.WriteNumber("from", s.From);
                    w.WriteNumber("to", s.To);
                    w.WriteNumber("lines", s.TotalLines);
                    w.WriteString("encoding", s.EncodingName);
                    w.WriteString("line_ending", s.LineEnding);
                    w.WriteBoolean("final_newline", s.FinalNewline);
                    w.WriteString("hash", s.Hash);
                    w.WriteNumber("bytes", s.Bytes);
                    WriteFacts(w, s.Facts);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", result.Value.Count);
            }));

        var text = new StringBuilder();
        foreach (ReadSlice s in result.Value)
        {
            text.Append(Show(bench, s.Path)).Append("  ").Append(s.EncodingName)
                .Append("  ").Append(s.LineEnding)
                .Append("  final-newline: ").Append(s.FinalNewline ? "yes" : "no")
                .Append("  lines ").Append(s.From).Append('-').Append(s.To)
                .Append(" of ").AppendLine(s.TotalLines.ToString());

            int number = s.From;
            foreach (Line line in TextIo.SplitLines(s.Text))
                text.Append((number++).ToString().PadLeft(5)).Append(" | ").AppendLine(line.Text);
        }

        return Ok(text.ToString());
    }

    static CliResult TaskList(Bench bench, bool json)
    {
        Result<IReadOnlyList<TaskDecl>> tasks = bench.TaskList();
        if (!tasks.IsOk) return Failed(tasks.Failure!, json);

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("tasks");
                foreach (TaskDecl t in tasks.Value)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteStartArray("command");
                    foreach (string part in t.Command) w.WriteStringValue(part);
                    w.WriteEndArray();
                    if (t.WorkingDirectory is { } cwd) w.WriteString("cwd", cwd);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", tasks.Value.Count);
            }));

        if (tasks.Value.Count == 0)
            return Ok($"no tasks declared (there is no {Tasks.FileName} at the root){Environment.NewLine}");

        var text = new StringBuilder();
        foreach (TaskDecl t in tasks.Value)
            text.Append(t.Name.PadRight(12)).AppendLine(t.Display);

        return Ok(text.ToString());
    }

    /// <summary>
    /// R8.8: a caller can ask what it is bounded to.
    ///
    /// <para>Without this the only way to discover the boundary is to be
    /// refused by it, which is learning by trial against a security
    /// check. It matters most on the MCP front end, where the model never
    /// sees the command line that declared the roots and so has no other
    /// way to know they exist, what they are called, or which one an
    /// unqualified path lands in.</para>
    /// </summary>
    static CliResult RootList(Bench bench, bool json)
    {
        IReadOnlyList<string> names = bench.Roots.Names;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("roots");
                foreach (string name in names)
                {
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("path", bench.Roots.PathOf(name));
                    w.WriteBoolean("default", name.Equals(names[0], Core.Roots.PathComparison));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", names.Count);
                w.WriteString("source", bench.Roots.Origin);
            }));

        var text = new StringBuilder();
        foreach (string name in names)
            text.Append(name.PadRight(12)).Append(bench.Roots.PathOf(name))
                .AppendLine(name.Equals(names[0], Core.Roots.PathComparison) ? "  (default)" : "");

        // Where the boundary came from, not only what it is: a caller
        // who disagrees with it has to know which file to open.
        text.AppendLine().Append("declared by ").AppendLine(bench.Roots.Origin);

        return Ok(text.ToString());
    }

    // ---- mutating verbs ----


    static async Task<CliResult> Write(Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        string? path = args.At(0);
        if (path is null) return Failed(new Failure(Outcome.Invalid, "write needs a path"), json);

        // R5.11's reasoning applied to write: the source of the content
        // is explicit, and never inferred. Falling back to stdin when
        // nothing was said made `fettle write PATH` sit and block on a
        // stream nobody was going to write to - the same hang that Tasks
        // deliberately prevents by closing a child's stdin, left
        // unguarded in Fettler's own front end.
        string text;
        if (args.Value("text") is { } inline) text = inline;
        else if (args.Value("text-file") is { } file)
        {
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(new Failure(Outcome.NotFound, $"cannot read --text-file: {e.Message}", file), json);
            }
        }
        else if (args.Has("stdin"))
            text = TextIo.WithoutMark(await stdin.ReadToEndAsync(cancel).ConfigureAwait(false));
        else return Failed(new Failure(Outcome.Invalid,
            "write needs its content named: --stdin, --text TEXT, or --text-file PATH"), json);


        Result<Saved> saved = await bench.WriteAsync(
            path, text, args.Has("overwrite"), args.Value("encoding"), args.Value("eol"), cancel)
            .ConfigureAwait(false);

        if (!saved.IsOk) return Failed(saved.Failure!, json);
        Saved s = saved.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("path", Show(bench, s.Path));
                w.WriteBoolean("created", s.Created);
                w.WriteNumber("bytes", s.Bytes);
                w.WriteString("encoding", s.EncodingName);
                w.WriteString("line_ending", TextIo.EndingName(s.LineEnding));
                w.WriteBoolean("final_newline", s.FinalNewline);
                w.WriteString("hash", s.Hash);
                WriteLost(w, s.MetadataLost);
            }));

        var human = new StringBuilder()
            .Append(s.Created ? "wrote " : "replaced ").Append(Show(bench, s.Path))
            .Append("  ").Append(s.Bytes).Append(" bytes  ").Append(s.EncodingName)
            .Append("  ").Append(TextIo.EndingName(s.LineEnding))
            .Append("  final-newline: ").AppendLine(s.FinalNewline ? "yes" : "no");

        AppendLost(human, s.MetadataLost);
        return Ok(human.ToString());
    }

    static async Task<CliResult> Edit(Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        Result<IReadOnlyList<FileEdits>> batch = EditScript.Build(args, stdin);
        if (!batch.IsOk) return Failed(batch.Failure!, json);

        Result<EditAnswer> applied = await bench.EditAsync(batch.Value, args.Has("dry-run"), cancel)
            .ConfigureAwait(false);

        if (!applied.IsOk) return Failed(applied.Failure!, json);
        EditAnswer answer = applied.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteBoolean("dry_run", answer.DryRun);
                w.WriteStartArray("plan");
                foreach (PlannedEdit p in answer.Plan)
                {
                    w.WriteStartObject();
                    w.WriteString("path", p.Path);
                    w.WriteNumber("edit", p.Index);
                    w.WriteNumber("line", p.Line);
                    w.WriteString("description", p.Description);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("files");
                foreach (EditedFile f in answer.Files)
                {
                    w.WriteStartObject();
                    w.WriteString("path", Show(bench, f.Path));
                    w.WriteNumber("edits", f.EditsApplied);
                    w.WriteString("hash", f.Hash);
                    WriteLost(w, f.MetadataLost);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));

        var human = new StringBuilder();
        if (answer.DryRun)
        {
            human.Append("would apply ").Append(answer.Plan.Count)
                 .Append(answer.Plan.Count == 1 ? " edit" : " edits").AppendLine();
            foreach (PlannedEdit p in answer.Plan)
                human.Append("   edit ").Append(p.Index).Append("  ").Append(p.Path)
                     .Append(" line ").Append(p.Line).Append("  ").AppendLine(p.Description);
        }
        else
        {
            int edits = 0;
            foreach (EditedFile f in answer.Files) edits += f.EditsApplied;
            human.Append("applied ").Append(edits).Append(edits == 1 ? " edit to " : " edits to ")
                 .Append(answer.Files.Count).AppendLine(answer.Files.Count == 1 ? " file" : " files");
            foreach (EditedFile f in answer.Files)
            {
                human.Append("   ").Append(Show(bench, f.Path)).Append("  ").AppendLine(f.Hash[..12]);
                AppendLost(human, f.MetadataLost);
            }
        }

        return Ok(human.ToString());
    }

    static async Task<CliResult> Replace(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        string? find = args.At(0);
        string? with = args.At(1);
        if (find is null || with is null)
            return Failed(new Failure(Outcome.Invalid, "replace needs the text to find and the text to put there"), json);

        Result<ReplaceAnswer> result = await bench.ReplaceAsync(
            find, with, args.Value("glob"), args.Value("in"), args.Has("dry-run"),
            args.Has("include-generated"), cancel).ConfigureAwait(false);

        if (!result.IsOk) return Failed(result.Failure!, json);
        ReplaceAnswer answer = result.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteBoolean("dry_run", answer.DryRun);
                w.WriteNumber("occurrences", answer.TotalOccurrences);
                w.WriteStartArray("files");
                foreach (ReplacePlan p in answer.Files)
                {
                    w.WriteStartObject();
                    w.WriteString("path", p.Path);
                    w.WriteNumber("occurrences", p.Occurrences);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", answer.Files.Count);
            }));

        var human = new StringBuilder()
            .Append(answer.DryRun ? "would change " : "changed ")
            .Append(answer.Files.Count).Append(answer.Files.Count == 1 ? " file, " : " files, ")
            .Append(answer.TotalOccurrences)
            .AppendLine(answer.TotalOccurrences == 1 ? " occurrence" : " occurrences");

        foreach (ReplacePlan p in answer.Files)
            human.Append("   ").Append(p.Path.PadRight(44)).AppendLine(p.Occurrences.ToString());

        return Ok(human.ToString());
    }

    static async Task<CliResult> Move(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } from || args.At(1) is not { } to)
            return Failed(new Failure(Outcome.Invalid, "move needs a source and a destination"), json);

        Result<MoveReport> moved = await bench.MoveAsync(from, to, args.Has("overwrite"), cancel).ConfigureAwait(false);
        if (!moved.IsOk) return Failed(moved.Failure!, json);
        MoveReport m = moved.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("from", Show(bench, m.From));
                w.WriteString("to", Show(bench, m.To));
                w.WriteBoolean("atomic", m.Atomic);
                w.WriteBoolean("case_only", m.CaseOnly);
                w.WriteBoolean("crossed_root", m.CrossedRoot);
                w.WriteBoolean("directory", m.Directory);
            }));

        var human = new StringBuilder()
            .Append("moved ").Append(Show(bench, m.From)).Append(" -> ").Append(Show(bench, m.To))
            .Append("  atomic: ").Append(m.Atomic ? "yes" : "no");
        if (m.CaseOnly) human.Append("  case-only rename");
        if (m.CrossedRoot) human.Append("  crossed roots");
        return Ok(human.AppendLine().ToString());
    }

    static async Task<CliResult> Copy(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } from || args.At(1) is not { } to)
            return Failed(new Failure(Outcome.Invalid, "copy needs a source and a destination"), json);

        Result<CopyReport> copied = await bench.CopyAsync(
            from, to, args.Has("recursive"), args.Has("overwrite"), args.Has("preserve-times"), cancel)
            .ConfigureAwait(false);

        if (!copied.IsOk) return Failed(copied.Failure!, json);
        CopyReport c = copied.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("from", Show(bench, c.From));
                w.WriteString("to", Show(bench, c.To));
                w.WriteNumber("files", c.Files);
                w.WriteNumber("directories", c.Directories);
                w.WriteNumber("bytes", c.Bytes);
                w.WriteBoolean("crossed_root", c.CrossedRoot);
                w.WriteStartArray("links_skipped");
                foreach (string s in c.LinksSkipped) w.WriteStringValue(s);
                w.WriteEndArray();
            }));

        var human = new StringBuilder()
            .Append("copied ").Append(Show(bench, c.From)).Append(" -> ").Append(Show(bench, c.To))
            .Append("  ").Append(c.Files).Append(c.Files == 1 ? " file" : " files");
        if (c.Directories > 0) human.Append("  ").Append(c.Directories).Append(c.Directories == 1 ? " directory" : " directories");
        if (c.CrossedRoot) human.Append("  crossed roots");
        human.AppendLine();

        foreach (string skipped in c.LinksSkipped)
            human.Append("   not copied (a link): ").AppendLine(skipped);

        return Ok(human.ToString());
    }

    static async Task<CliResult> Delete(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } path)
            return Failed(new Failure(Outcome.Invalid, "delete needs a path"), json);

        Result<DeleteReport> deleted = await bench.DeleteAsync(path, args.Has("recursive"), args.Has("force"), cancel)
            .ConfigureAwait(false);

        if (!deleted.IsOk) return Failed(deleted.Failure!, json);
        DeleteReport d = deleted.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("path", Show(bench, d.Path));
                w.WriteNumber("files", d.Files);
                w.WriteNumber("directories", d.Directories);
            }));

        var human = new StringBuilder().Append("deleted ").Append(Show(bench, d.Path));
        if (d.Directories > 0)
            human.Append("  ").Append(d.Files).Append(d.Files == 1 ? " file  " : " files  ")
                 .Append(d.Directories).Append(d.Directories == 1 ? " directory" : " directories");
        return Ok(human.AppendLine().ToString());
    }

    static async Task<CliResult> Exec(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } path)
            return Failed(new Failure(Outcome.Invalid, "exec needs a path"), json);

        if (args.Has("on") == args.Has("off"))
            return Failed(new Failure(Outcome.Invalid, "exec needs --on or --off"), json);

        Result<ExecReport> set = await bench.SetExecutableAsync(path, args.Has("on"), cancel).ConfigureAwait(false);
        if (!set.IsOk) return Failed(set.Failure!, json);
        ExecReport e = set.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("path", Show(bench, e.Path));
                w.WriteBoolean("executable", e.Executable);
                w.WriteBoolean("supported", e.Supported);
            }));

        return Ok(e.Supported
            ? $"{Show(bench, e.Path)} executable: {(e.Executable ? "yes" : "no")}{Environment.NewLine}"
            : $"{Show(bench, e.Path)} unchanged: this filesystem has no execute bit{Environment.NewLine}");
    }

    static async Task<CliResult> Run(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } name)
            return Failed(new Failure(Outcome.Invalid, "run needs the name of a declared task"), json);

        var timeout = TimeSpan.FromSeconds(args.Int("timeout", 0));
        Result<TaskRun> ran = await bench.RunAsync(name, timeout, cancel).ConfigureAwait(false);
        if (!ran.IsOk) return Failed(ran.Failure!, json);
        TaskRun r = ran.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("task", r.Name);
                w.WriteNumber("exit_code", r.ExitCode);
                w.WriteString("stdout", r.Stdout);
                w.WriteString("stderr", r.Stderr);
                w.WriteNumber("seconds", Math.Round(r.Duration.TotalSeconds, 3));
                w.WriteBoolean("timed_out", r.TimedOut);
            }));

        var human = new StringBuilder()
            .Append(r.Name).Append(": exit ").Append(r.ExitCode)
            .Append(" in ").Append(r.Duration.TotalSeconds.ToString("0.0")).AppendLine("s");

        if (r.Stdout.Length > 0) human.Append("[stdout] ").AppendLine(r.Stdout.TrimEnd());
        if (r.Stderr.Length > 0) human.Append("[stderr] ").AppendLine(r.Stderr.TrimEnd());

        // The task's own exit code is not this command's: `run` succeeded
        // in running it. A caller wanting the child's code reads it from
        // the answer, which is why it is in both output modes.
        return Ok(human.ToString());
    }

    static async Task<CliResult> Batch(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        Result<BatchAnswer> ran;
        if (args.Value("script-inline") is { } inline)
            ran = await BatchScript.RunInlineAsync(bench, inline, cancel).ConfigureAwait(false);
        else if (args.Value("script") is { } script)
            ran = await BatchScript.RunAsync(bench, script, cancel).ConfigureAwait(false);
        else
            return Failed(new Failure(Outcome.Invalid, "batch needs --script FILE"), json);

        if (!ran.IsOk) return Failed(ran.Failure!, json);
        BatchAnswer answer = ran.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("operations");
                foreach (BatchStep s in answer.Steps)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", s.Index);
                    w.WriteString("op", s.Op);
                    w.WriteString("outcome", ExitCodes.NameOf(s.Outcome));
                    w.WriteString("detail", s.Detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("completed", answer.Completed);
                w.WriteNumber("count", answer.Steps.Count);
                w.WriteBoolean("stopped_early", answer.StoppedEarly);
            }));

        var human = new StringBuilder();
        foreach (BatchStep s in answer.Steps)
            human.Append(s.Index.ToString().PadLeft(2)).Append("  ").Append(s.Op.PadRight(8))
                 .Append(ExitCodes.NameOf(s.Outcome).PadRight(14)).AppendLine(s.Detail);

        human.Append(answer.Steps.Count).Append(answer.Steps.Count == 1 ? " operation, " : " operations, ")
             .Append(answer.Completed).AppendLine(" ok");

        if (answer.StoppedEarly)
            human.AppendLine("stopped at the first failure; the operations above it were done and are not undone");

        return new CliResult(answer.StoppedEarly ? ExitCodes.Refused : ExitCodes.Ok, human.ToString(), string.Empty);
    }

    // ---- rendering ----

    static async Task<CliResult> Simple<T>(Task<Result<T>> operation, bool json, Func<T, string> human)
    {
        Result<T> result = await operation.ConfigureAwait(false);
        if (!result.IsOk) return Failed(result.Failure!, json);

        if (!json) return Ok(human(result.Value) + Environment.NewLine);

        string message = human(result.Value);
        return Ok(Json(w => w.WriteString("detail", message)));
    }

    static CliResult Ok(string stdout) => new(ExitCodes.Ok, stdout, string.Empty);

    /// <summary>
    /// R3.7 in one place: a failure in machine-readable mode is a
    /// complete result on <b>stdout</b>, never on stderr, because a
    /// PowerShell 5.1 caller cannot retrieve stderr without turning it
    /// into a terminating error.
    /// </summary>
    static CliResult Failed(Failure failure, bool json)
    {
        int code = ExitCodes.Of(failure.Outcome);

        if (json)
            return new CliResult(code, JsonBody(w =>
            {
                w.WriteBoolean("ok", false);
                w.WriteString("outcome", ExitCodes.NameOf(failure.Outcome));
                w.WriteString("message", failure.Message);
                if (failure.Path is { } p) w.WriteString("path", p);
            }), string.Empty);

        return new CliResult(code, string.Empty,
            $"refused: {failure.Message}" + (failure.Path is null ? "" : $" ({failure.Path})") + Environment.NewLine);
    }

    static string Json(Action<Utf8JsonWriter> body) => JsonBody(w =>
    {
        w.WriteBoolean("ok", true);
        body(w);
    });

    static string JsonBody(Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine;
    }

    static void WriteFacts(Utf8JsonWriter w, FileFacts facts)
    {
        if (facts.Executable is { } x) w.WriteBoolean("executable", x);
        if (facts.ReadOnly) w.WriteBoolean("read_only", true);
        if (facts.Hidden) w.WriteBoolean("hidden", true);
        if (facts.Link != LinkKind.None)
        {
            w.WriteString("link", facts.Link == LinkKind.Junction ? "junction" : "symlink");
            if (facts.LinkTarget is { } t) w.WriteString("link_target", t);
        }
    }

    static void WriteLost(Utf8JsonWriter w, IReadOnlyList<string> lost)
    {
        if (lost.Count == 0) return;
        w.WriteStartArray("metadata_not_preserved");
        foreach (string l in lost) w.WriteStringValue(l);
        w.WriteEndArray();
    }

    static void AppendLost(StringBuilder text, IReadOnlyList<string> lost)
    {
        foreach (string l in lost)
            text.Append("   warning: could not carry over ").AppendLine(l);
    }

    /// <summary>Paths are written qualified only when more than one root
    /// is open, so a single-root caller sees exactly what it always
    /// did (R8.7).</summary>
    static string Show(Bench bench, ContainedPath path) =>
        bench.Roots.IsSingle ? path.Display : path.Qualified;

    static string Help() => """
        fettle - find, search, read, write, edit, move, copy, delete, and run a declared task.

        Global flags, accepted by every verb:
          --root NAME=PATH     a root, repeatable; paths are then written name:path.
                               Prefer an ABSOLUTE path: a relative one binds the
                               boundary to the current directory, so the same
                               command means different things from different
                               places. `roots` says what is declared, and where
                               it was declared.
                               With no --root at all, the nearest .fettler.json
                               at or above the current directory declares them,
                               and its paths are read relative to ITSELF - so
                               the boundary is the same from anywhere in the
                               tree. Failing that, the current directory.
          --config PATH        use this .fettler.json rather than searching
          --no-config          do not search; the current directory is the root
          --json               the machine-readable result, complete, on stdout
          --include-generated  do not skip bin, obj, .git, artifacts, node_modules
          --exclude GLOB       skip more, repeatable

        Verbs:
          find PATTERN [--sort mtime] [--limit N] [--since TIME]
          search PATTERN [-e PATTERN]... [--glob G] [--literal] [--case-sensitive]
                         [--context N] [--limit N] [--count] [--files-only]
                 A pattern may instead arrive as --pattern-file PATH or
                 --pattern-stdin, one per line, so a shell never gets to
                 eat a quote out of it.
          read PATH... [--from N] [--to N]
          write PATH --stdin [--overwrite] [--encoding E] [--eol lf|crlf]
          edit PATH [--expect HASH] [--dry-run] and one of:
                 --replace S --with S [--between 120-140] [--all]
                 --insert-after N --text S
                 --delete 150-151
                 --script FILE
               Any of --replace, --with and --text may instead be given as
               --NAME-file PATH or --NAME-stdin, so text with quotes and
               newlines in it never has to be escaped onto a command line.
          replace FIND WITH [--glob G] [--dry-run]
          new PATH
          mkdir PATH
          move FROM TO [--overwrite]
          copy FROM TO [--recursive] [--overwrite] [--preserve-times]
          delete PATH [--recursive] [--force]
          exec PATH --on | --off
          tasks
          roots                 the declared roots, their paths, and which is default
          run NAME [--timeout SECONDS]
          batch --script FILE
          serve                 become the MCP front end on stdio

        An unknown flag is refused, never ignored: ignoring one would eat the
        argument after it and answer a different question confidently.

        Exit codes: 0 ok, 2 invalid, 3 not-found, 4 outside-root, 5 target-exists,
                    6 stale, 7 conflict, 8 refused, 9 denied, 10 timed-out.

        """;
}
