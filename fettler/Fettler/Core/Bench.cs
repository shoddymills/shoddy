namespace Fettler.Core;

public sealed record FindRequest(
    string? Root = null,
    string? Pattern = null,
    bool IncludeGenerated = false,
    IReadOnlyList<string>? Exclude = null,
    DateTimeOffset? Since = null,
    int Limit = 1000,
    bool ByModified = false);

public sealed record SearchRequest(
    IReadOnlyList<string> Patterns,
    string? Root = null,
    string? Glob = null,
    PatternKind Kind = PatternKind.Regex,
    bool CaseSensitive = false,
    int Context = 0,
    int Limit = 200,
    bool IncludeGenerated = false,
    IReadOnlyList<string>? Exclude = null);

/// <summary>
/// What to read, and which part of it.
///
/// <para><c>Tail</c> is the end of the file: the last N lines, whatever
/// the length. It exists because a log is the one file read backwards,
/// and without it the end of one costs two calls - a read to learn the
/// line count, then arithmetic to name a range. A tool that cannot
/// cheaply answer "what did it just say" sends its caller to a shell,
/// which is the thing this tool exists to make unnecessary.</para>
///
/// <para><c>Tail</c> with <c>From</c> or <c>To</c> is refused rather than
/// resolved by precedence: the end of the file and a named range are two
/// different questions, and picking one silently answers the other.</para>
/// </summary>
public sealed record ReadRequest(
    IReadOnlyList<string> Paths, int? From = null, int? To = null, int? Tail = null,
    string? Member = null);

/// <summary>What an extraction did: how many members were written, and
/// where they landed.</summary>
public sealed record Unpacked(ContainedPath Archive, ContainedPath Into, int Members, long Bytes);

/// <summary>A slice of one file, as <c>read</c> answers it (R4.1).</summary>
public sealed record ReadSlice(
    ContainedPath Path,
    string Text,
    int From,
    int To,
    int TotalLines,
    string EncodingName,
    string LineEnding,
    bool MixedEndings,
    bool FinalNewline,
    string Hash,
    long Bytes,
    FileFacts Facts,
    FileKind Kind = FileKind.Text,
    ImageFacts? Image = null)
{
    /// <summary>Whether the answer stops short of the end of the file.
    /// Named rather than left to be worked out from three numbers,
    /// because a truncated answer that reads as a complete one is how a
    /// caller confidently acts on half a file (R4.10).</summary>
    public bool Truncated => To < TotalLines;

    public int Remaining => Math.Max(0, TotalLines - To);
}

public sealed record ReplacePlan(string Path, int Occurrences);

public sealed record ReplaceAnswer(
    IReadOnlyList<ReplacePlan> Files,
    int TotalOccurrences,
    bool DryRun,
    IReadOnlyList<EditedFile> Written);

/// <summary>
/// The callable core of R3.1: operations take typed arguments and answer
/// typed results, and neither a protocol nor a console nor an exit code
/// appears anywhere in it. Both front ends sit on this, which is what
/// makes R3.4's parity a property of the design rather than a promise
/// that has to be re-kept every time a verb is added.
///
/// <para><b>The execution model of R3.11 is enforced here.</b>
/// Read-only operations - find, search, read, tasks - touch nothing and
/// run concurrently. Every operation that mutates the tree takes one
/// exclusive lock for its duration. Per-path locking is the finer answer
/// and would earn its complexity if mutations were slow; they are
/// millisecond operations on local files, so the coarse lock costs
/// nothing measurable and removes an entire class of interleaving defect
/// instead of documenting it. <c>run</c> holds neither lock: it is a
/// child process of arbitrary duration, and blocking the tree for it
/// would reintroduce exactly the stall R3.8 removes.</para>
/// </summary>
public sealed class Bench : IDisposable
{
    readonly SemaphoreSlim mutation = new(1, 1);

    public Bench(Roots roots) => Roots = roots;

    public Roots Roots { get; }

    public void Dispose() => mutation.Dispose();

    // ---- read-only: no lock, freely concurrent ----

    public Bounded<FoundFile> Find(FindRequest request)
    {
        Glob? pattern = null;
        if (request.Pattern is { } p)
        {
            Result<Glob> compiled = Glob.Compile(p);
            if (!compiled.IsOk) return new Bounded<FoundFile>([], false, 0);
            pattern = compiled.Value;
        }

        return Tree.Find(Roots, request.Root ?? Roots.Names[0], pattern,
            ExcludesFor(request.IncludeGenerated, request.Exclude),
            request.Since, request.Limit, request.ByModified);
    }

    public Result<Bounded<FoundFile>> FindChecked(FindRequest request)
    {
        if (request.Pattern is { } p)
        {
            Result<Glob> compiled = Glob.Compile(p);
            if (!compiled.IsOk) return compiled.Carry<Bounded<FoundFile>>();
        }

        Result<string> root = RootNamed(request.Root);
        if (!root.IsOk) return root.Carry<Bounded<FoundFile>>();

        return Result<Bounded<FoundFile>>.Ok(Find(request with { Root = root.Value }));
    }

    public Result<SearchAnswer> Search(SearchRequest request)
    {
        Result<string> root = RootNamed(request.Root);
        if (!root.IsOk) return root.Carry<SearchAnswer>();

        Glob? glob = null;
        if (request.Glob is { } g)
        {
            Result<Glob> compiled = Glob.Compile(g);
            if (!compiled.IsOk) return compiled.Carry<SearchAnswer>();
            glob = compiled.Value;
        }

        // No limit on the file sweep: R4.10's bound is on hits, and
        // stopping the walk early would make which files were searched
        // depend on the limit.
        Bounded<FoundFile> files = Tree.Find(Roots, root.Value, glob,
            ExcludesFor(request.IncludeGenerated, request.Exclude), null, 0, byModified: false);

        return Searcher.Search(files.Items, request.Patterns, request.Kind,
            request.CaseSensitive, request.Context, request.Limit, files.Excluded);
    }

    /// <summary>
    /// R4.15: several paths in one call. Not an optimisation of the tool,
    /// whose own cost per call is small, but of the caller's - for the
    /// MCP front end a call costs a model turn, which is seconds and a
    /// block of context against a few milliseconds of reading.
    /// </summary>
    /// <summary>
    /// How many lines <c>read</c> answers when the caller did not say.
    ///
    /// <para><b>9b.8, and it is a context bug rather than a safety
    /// one.</b> <c>find</c> stops at a thousand and <c>search</c> at two
    /// hundred; <c>read</c> had no limit at all and answered the whole
    /// file. The assistant's own reader truncates at two thousand - so
    /// denying that one and routing everything through an uncapped
    /// <c>read</c> would have made the context budget WORSE, quietly, and
    /// the tool would have got the blame for feeling slow.</para>
    ///
    /// <para>Two thousand matches what it replaces, so nobody has to
    /// relearn a number. Saying <c>to</c> lifts the cap: an explicit
    /// range is a caller who knows what they are asking for.</para>
    /// </summary>
    public const int DefaultLineCap = 2000;

    public Result<IReadOnlyList<ReadSlice>> Read(ReadRequest request)
    {
        if (request.Paths.Count == 0)
            return Result<IReadOnlyList<ReadSlice>>.Fail(Outcome.Invalid, "no path was given to read");

        var slices = new List<ReadSlice>(request.Paths.Count);
        foreach (string given in request.Paths)
        {
            Result<ContainedPath> path = Roots.Resolve(given, Permission.Read);
            if (!path.IsOk) return path.Carry<IReadOnlyList<ReadSlice>>();

            Result<ReadSlice> one = ReadOne(path.Value, request);
            if (!one.IsOk) return one.Carry<IReadOnlyList<ReadSlice>>();
            slices.Add(one.Value);
        }

        return Result<IReadOnlyList<ReadSlice>>.Ok(slices);
    }

    Result<ReadSlice> ReadOne(ContainedPath path, ReadRequest request)
    {
        FileKind kind = Typed.KindOf(path.Full);

        // An image has no lines, so the line machinery below would answer
        // nonsense. It gets its own shape: the facts, and the bytes for
        // whoever can show them.
        if (kind == FileKind.Image)
        {
            Result<ImageFacts> image = Typed.ReadImage(path);
            if (!image.IsOk) return image.Carry<ReadSlice>();

            return Result<ReadSlice>.Ok(new ReadSlice(
                path, string.Empty, 0, 0, 0, "binary", "none", false, false,
                TextIo.HashOf(image.Value.Content), image.Value.Bytes,
                Tree.Facts(path.Full, isDirectory: false), kind, image.Value));
        }

        // A member of an archive, and a lone gzip, both arrive as bytes
        // rather than as a file on disk. Both are decoded by the ordinary
        // rules from there, so a .md inside a zip comes back with an
        // encoding and a line ending like any other .md - and nothing is
        // unpacked to disk to do it.
        Result<byte[]>? unpacked = null;

        if (request.Member is { Length: > 0 } wanted)
        {
            if (kind != FileKind.Archive)
                return Result<ReadSlice>.Fail(Outcome.Invalid,
                    "--member names an entry inside an archive, and this is not one", path.Display);

            unpacked = Archives.Member(path, wanted);
        }
        else if (kind == FileKind.Gzip)
        {
            unpacked = Archives.Gunzip(path);
        }

        TextFile file;

        if (unpacked is { } bytes)
        {
            if (!bytes.IsOk) return bytes.Carry<ReadSlice>();

            Result<TextFile> decoded = TextIo.Decode(path, bytes.Value);
            if (!decoded.IsOk) return decoded.Carry<ReadSlice>();
            file = decoded.Value;
        }
        else if (kind is FileKind.Notebook or FileKind.Pdf or FileKind.Archive)
        {
            // Rendered to text and then sliced like any other text, so
            // --from, --to and --tail mean the same thing for every kind
            // and a caller has one rule to learn.
            Result<string> rendered = kind switch
            {
                FileKind.Notebook => Typed.ReadNotebook(path),
                FileKind.Pdf => Typed.ReadPdf(path),
                _ => Manifest(path),
            };

            if (!rendered.IsOk) return rendered.Carry<ReadSlice>();

            Result<byte[]> raw = TextIo.ReadBytes(path);
            if (!raw.IsOk) return raw.Carry<ReadSlice>();

            IReadOnlyList<Line> lines = TextIo.SplitLines(rendered.Value);
            file = new TextFile(path, rendered.Value, lines, "utf-8", LineEnding.Lf, false,
                lines.Count > 0 && lines[^1].Ending.Length > 0,
                // The hash is of the FILE, not of the rendering, because
                // its job is to prove the file on disk has not moved.
                TextIo.HashOf(raw.Value), raw.Value.Length);
        }
        else
        {
            Result<TextFile> read = TextIo.Read(path);
            if (!read.IsOk) return read.Carry<ReadSlice>();
            file = read.Value;
        }

        int from;
        int to;

        if (request.Tail is { } tail)
        {
            // The end of the file, named from the end, so the caller needs
            // to know neither the length nor any arithmetic. No default
            // cap applies: the caller named the number of lines, exactly
            // as an explicit `to` names one.
            to = file.LineCount;
            from = Math.Max(1, file.LineCount - tail + 1);
        }
        else
        {
            from = Math.Max(1, request.From ?? 1);
            to = request.To ?? Math.Min(file.LineCount, from + DefaultLineCap - 1);
            to = Math.Min(file.LineCount, to);
        }

        if (to < from) to = from - 1;

        string text = to < from
            ? string.Empty
            : TextIo.Join(file.Lines.Skip(from - 1).Take(to - from + 1));

        return Result<ReadSlice>.Ok(new ReadSlice(
            path, text, from, to, file.LineCount, file.EncodingName,
            TextIo.EndingName(file.LineEnding), file.MixedEndings, file.FinalNewline,
            file.Hash, file.Bytes,
            Tree.Facts(path.Full, isDirectory: false), kind));
    }

    static Result<string> Manifest(ContainedPath path)
    {
        Result<IReadOnlyList<ArchiveMember>> members = Archives.Members(path);
        return members.IsOk
            ? Result<string>.Ok(Archives.Manifest(members.Value))
            : members.Carry<string>();
    }

    public Result<IReadOnlyList<TaskDecl>> TaskList() => Tasks.Read(Roots);

    // ---- mutating: one exclusive lock, held for the operation ----

    // allowCredential defaults to false and is only ever passed by the
    // command line. The MCP front end has no way to reach it, which is
    // the point: overriding a credential refusal is a person's judgement
    // about their own file, in the same way `setup` is a person's act.
    public Task<Result<Saved>> WriteAsync(string path, string text, bool overwrite,
        string? encoding = null, string? lineEnding = null, CancellationToken cancel = default,
        bool allowCredential = false) =>
        Mutating(() =>
        {
            // Create if nothing is there, update if something is: the
            // caller wrote one command, but which permission it needs is
            // a fact about the tree, not about the wording.
            Result<ContainedPath> p = Roots.ResolveForWrite(path);
            if (!p.IsOk) return p.Carry<Saved>();

            // R4.2: a file that arrived as CRLF with a BOM leaves that
            // way unless the caller explicitly asks otherwise, so an
            // overwrite reads the existing shape before replacing it.
            string chosenEncoding = encoding ?? "utf-8";
            LineEnding chosenEnding = ParseEnding(lineEnding) ?? LineEnding.Lf;

            if (encoding is null || lineEnding is null)
            {
                Result<TextFile> existing = TextIo.Read(p.Value);
                if (existing.IsOk)
                {
                    if (encoding is null) chosenEncoding = existing.Value.EncodingName;
                    if (lineEnding is null) chosenEnding = existing.Value.LineEnding;
                }
            }

            return SafeWrite.Text(p.Value, Retext(text, chosenEnding), chosenEncoding, chosenEnding,
                                  overwrite, allowCredential);
        }, cancel);

    /// <summary>
    /// Unpack an archive into a declared tree.
    ///
    /// <para><b>Two passes, and the first one writes nothing.</b> Every
    /// member is resolved through the boundary and checked for permission
    /// before a byte is written, so an archive that would escape or that
    /// lands anywhere it may not is refused WHOLE. A half-extracted
    /// archive is the state nobody can see the shape of, which is the same
    /// reason <c>edit</c> is all-or-nothing.</para>
    ///
    /// <para><b>Zip slip is refused by construction</b>, not by a check
    /// bolted on: a member called <c>../../etc/passwd</c> resolves outside
    /// every tree and comes back <see cref="Outcome.OutsideRoot"/> like
    /// any other outside path.</para>
    ///
    /// <para><b>Link members are refused rather than skipped.</b> Creating
    /// links is a stated exclusion, and a symbolic link inside a tar is
    /// the one entry that can point out of the tree AFTER every path check
    /// has passed. Skipping it silently would produce an extraction that
    /// looks complete and is not, so the whole archive is refused and the
    /// reason is named.</para>
    ///
    /// <para><b>The executable bit is carried.</b> R6.9 is this project's
    /// own clause about that bit, and its release depends on a `fettle`
    /// arriving out of a tar still executable.</para>
    /// </summary>
    public Task<Result<Unpacked>> ExtractAsync(string archive, string into, bool overwrite,
        CancellationToken cancel = default) =>
        Mutating(() =>
        {
            Result<ContainedPath> source = Roots.Resolve(archive, Permission.Read);
            if (!source.IsOk) return source.Carry<Unpacked>();

            Result<ContainedPath> destination = Roots.Resolve(into, Permission.List);
            if (!destination.IsOk) return destination.Carry<Unpacked>();

            Result<IReadOnlyList<ArchiveMember>> members = Archives.Members(source.Value);
            if (!members.IsOk) return members.Carry<Unpacked>();

            // ---- pass one: nothing is written ----

            if (members.Value.Count > Archives.MaxMembers)
                return Result<Unpacked>.Fail(Outcome.Refused,
                    $"this archive names {members.Value.Count} members, past the {Archives.MaxMembers} "
                    + "this will unpack in one go", source.Value.Display);

            long total = 0;
            var targets = new Dictionary<string, ContainedPath>(StringComparer.Ordinal);

            foreach (ArchiveMember member in members.Value)
            {
                if (member.IsLink)
                    return Result<Unpacked>.Fail(Outcome.Refused,
                        $"'{member.Name}' is a link, and this does not create links - a link inside "
                        + "an archive is the one member that can point out of the tree after every "
                        + "path check has passed, so the archive is refused rather than part-unpacked",
                        source.Value.Display);

                if (member.IsDirectory) continue;

                total += member.Length;
                if (total > Archives.MaxTotalBytes)
                    return Result<Unpacked>.Fail(Outcome.Refused,
                        $"this archive unpacks to more than {Archives.MaxTotalBytes} bytes, which is "
                        + "where this stops rather than filling the disk", source.Value.Display);

                // Resolved through the boundary exactly like a path a
                // caller typed, which is what makes an escaping member an
                // ordinary outside-root refusal.
                string relative = into.Length == 0 ? member.Name : into.TrimEnd('/') + "/" + member.Name;
                Result<ContainedPath> target = overwrite
                    ? Roots.ResolveForWrite(relative)
                    : Roots.Resolve(relative, Permission.Create);

                if (!target.IsOk) return target.Carry<Unpacked>();

                // A member may climb out of the DESTINATION without
                // leaving the tree - `out/../elsewhere.txt` normalises to
                // somewhere the boundary is perfectly happy with, which is
                // how "extract into out" quietly scatters files across a
                // repository. The tree check cannot catch that one, because
                // nothing is wrong with the path; what is wrong is that it
                // is not where the caller said to put it.
                if (!Roots.IsWithin(destination.Value.Full, target.Value.Full))
                    return Result<Unpacked>.Fail(Outcome.Refused,
                        $"'{member.Name}' would land outside {into}, which is where this was told to "
                        + "put things; the archive is refused rather than part-unpacked",
                        source.Value.Display);

                if (!overwrite && File.Exists(target.Value.Full))
                    return Result<Unpacked>.Fail(Outcome.TargetExists,
                        $"'{member.Name}' is already there; pass --overwrite to replace what is",
                        target.Value.Display);

                targets[member.Name] = target.Value;
            }

            // ---- pass two: now it writes ----

            long written = 0;
            Result<int> unpacked = Archives.Unpack(source.Value, (member, content) =>
            {
                if (!targets.TryGetValue(member.Name, out ContainedPath? target)) return null;

                string? parent = Path.GetDirectoryName(target.Full);
                if (parent is not null) Directory.CreateDirectory(parent);

                using (var file = new FileStream(target.Full, FileMode.Create, FileAccess.Write))
                    content.CopyTo(file);

                written += member.Length;
                if (member.IsExecutable) FileOps.MarkExecutable(target.Full);
                return null;
            });

            if (!unpacked.IsOk) return unpacked.Carry<Unpacked>();

            return Result<Unpacked>.Ok(
                new Unpacked(source.Value, destination.Value, unpacked.Value, written));
        }, cancel);

    public Task<Result<EditAnswer>> EditAsync(IReadOnlyList<FileEdits> batch, bool dryRun,
        CancellationToken cancel = default, bool allowCredential = false) =>
        // A dry run writes nothing, but it still reads every file in the
        // batch and must not see them half-changed by a concurrent edit.
        Mutating(() => Editor.Apply(Roots, batch, dryRun, allowCredential), cancel);

    /// <summary>
    /// R5.10: the same substitution everywhere it appears, dry run first.
    ///
    /// <para>This takes a glob where R6.4 refuses one to every
    /// destructive operation, and the distinction is argued rather than
    /// assumed. A delete is unrecoverable and unreviewable, which is the
    /// whole of R6.4's reasoning. A replace prints its blast radius
    /// before it runs, is refused whole if any part cannot be resolved,
    /// and leaves every prior version in version control. The reasoning
    /// does not transfer, and nothing here loosens R6.4 for anything
    /// else.</para>
    /// </summary>
    public Task<Result<ReplaceAnswer>> ReplaceAsync(string find, string with, string? glob,
        string? root, bool dryRun, bool includeGenerated, CancellationToken cancel = default) =>
        Mutating(() =>
        {
            if (find.Length == 0)
                return Result<ReplaceAnswer>.Fail(Outcome.Invalid, "replace was given nothing to look for");

            Result<string> rootName = RootNamed(root);
            if (!rootName.IsOk) return rootName.Carry<ReplaceAnswer>();

            Glob? pattern = null;
            if (glob is { } g)
            {
                Result<Glob> compiled = Glob.Compile(g);
                if (!compiled.IsOk) return compiled.Carry<ReplaceAnswer>();
                pattern = compiled.Value;
            }

            Bounded<FoundFile> files = Tree.Find(Roots, rootName.Value, pattern,
                ExcludesFor(includeGenerated, null), null, 0, byModified: false);

            var plans = new List<ReplacePlan>();
            var batch = new List<FileEdits>();
            int total = 0;

            foreach (FoundFile file in files.Items)
            {
                Result<TextFile> read = TextIo.Read(file.Path);
                if (!read.IsOk) continue;              // binary, or unreadable: nothing to substitute

                int occurrences = Count(read.Value.Text, find);
                if (occurrences == 0) continue;

                total += occurrences;
                plans.Add(new ReplacePlan(file.Path.Display, occurrences));
                batch.Add(new FileEdits(file.Path.Full, read.Value.Hash,
                    [new Edit.Replace(find, with, All: true)]));
            }

            if (batch.Count == 0)
                return Result<ReplaceAnswer>.Ok(new ReplaceAnswer([], 0, dryRun, []));

            Result<EditAnswer> applied = Editor.Apply(Roots, batch, dryRun);
            if (!applied.IsOk) return applied.Carry<ReplaceAnswer>();

            return Result<ReplaceAnswer>.Ok(new ReplaceAnswer(plans, total, dryRun, applied.Value.Files));
        }, cancel);

    public Task<Result<MadeReport>> NewFileAsync(string path, CancellationToken cancel = default) =>
        Mutating(() => FileOps.NewFile(Roots, path), cancel);

    public Task<Result<MadeReport>> MakeDirectoryAsync(string path, CancellationToken cancel = default) =>
        Mutating(() => FileOps.MakeDirectory(Roots, path), cancel);

    public Task<Result<MoveReport>> MoveAsync(string from, string to, bool overwrite,
        CancellationToken cancel = default) =>
        Mutating(() => FileOps.Move(Roots, from, to, overwrite), cancel);

    public Task<Result<CopyReport>> CopyAsync(string from, string to, bool recursive,
        bool overwrite, bool preserveTimes, CancellationToken cancel = default) =>
        Mutating(() => FileOps.Copy(Roots, from, to, recursive, overwrite, preserveTimes), cancel);

    public Task<Result<DeleteReport>> DeleteAsync(string path, bool recursive, bool force,
        CancellationToken cancel = default) =>
        Mutating(() => FileOps.Delete(Roots, path, recursive, force), cancel);

    public Task<Result<ExecReport>> SetExecutableAsync(string path, bool executable,
        CancellationToken cancel = default) =>
        Mutating(() => FileOps.SetExecutable(Roots, path, executable), cancel);

    // ---- neither: a child process of arbitrary duration ----

    /// <summary>
    /// R3.11 again, and the reason it is worth stating: <c>run</c> takes
    /// no lock. A four-minute gate holding the mutation lock would stall
    /// every other operation for four minutes, which is the stall R3.8
    /// exists to remove. A task therefore sees the tree as it is, and
    /// Fettler does not undertake to hold it still.
    /// </summary>
    public Task<Result<TaskRun>> RunAsync(string name, TimeSpan timeout, CancellationToken cancel = default) =>
        Tasks.RunAsync(Roots, name, timeout, cancel);

    // ---- plumbing ----

    async Task<Result<T>> Mutating<T>(Func<Result<T>> operation, CancellationToken cancel)
    {
        try
        {
            await mutation.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Fail(Outcome.Refused, "the request was cancelled before it started");
        }

        try { return operation(); }
        finally { mutation.Release(); }
    }

    Result<string> RootNamed(string? name)
    {
        if (name is null) return Result<string>.Ok(Roots.Names[0]);

        foreach (string declared in Roots.Names)
            if (declared.Equals(name, Roots.PathComparison)) return Result<string>.Ok(declared);

        return Result<string>.Fail(Outcome.Invalid,
            $"no root called '{name}'; the roots are: {string.Join(", ", Roots.Names)}");
    }

    static Excludes ExcludesFor(bool includeGenerated, IReadOnlyList<string>? extra)
    {
        var globs = new List<Glob>();
        foreach (string pattern in extra ?? [])
        {
            Result<Glob> compiled = Glob.Compile(pattern);
            if (compiled.IsOk) globs.Add(compiled.Value);
        }

        return new Excludes(includeGenerated ? [] : Excludes.Generated, globs);
    }

    static LineEnding? ParseEnding(string? name) => name?.ToLowerInvariant() switch
    {
        "lf" => LineEnding.Lf,
        "crlf" => LineEnding.CrLf,
        "cr" => LineEnding.Cr,
        _ => null,
    };

    /// <summary>Give incoming text the file's line endings, so writing
    /// through Fettler does not turn a one-line change into a whole-file
    /// diff on a Windows checkout (R4.2).</summary>
    static string Retext(string text, LineEnding ending)
    {
        string want = TextIo.EndingText(ending);
        var rebuilt = new List<Line>();
        foreach (Line line in TextIo.SplitLines(text))
            rebuilt.Add(line.Ending.Length == 0 ? line : line with { Ending = want });
        return TextIo.Join(rebuilt);
    }

    static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
