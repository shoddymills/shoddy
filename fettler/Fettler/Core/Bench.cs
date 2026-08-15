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

public sealed record ReadRequest(IReadOnlyList<string> Paths, int? From = null, int? To = null);

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

        // A notebook and a PDF are rendered to text and then sliced like
        // any other text, so --from and --to mean the same thing for
        // every kind and a caller has one rule to learn.
        Result<string> rendered = kind switch
        {
            FileKind.Notebook => Typed.ReadNotebook(path),
            FileKind.Pdf => Typed.ReadPdf(path),
            _ => Result<string>.Ok(string.Empty),
        };

        if (!rendered.IsOk) return rendered.Carry<ReadSlice>();

        TextFile file;
        if (kind is FileKind.Notebook or FileKind.Pdf)
        {
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

        int from = Math.Max(1, request.From ?? 1);
        int to = request.To ?? Math.Min(file.LineCount, from + DefaultLineCap - 1);
        to = Math.Min(file.LineCount, to);
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

    public Result<IReadOnlyList<TaskDecl>> TaskList() => Tasks.Read(Roots);

    // ---- mutating: one exclusive lock, held for the operation ----

    public Task<Result<Saved>> WriteAsync(string path, string text, bool overwrite,
        string? encoding = null, string? lineEnding = null, CancellationToken cancel = default) =>
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

            return SafeWrite.Text(p.Value, Retext(text, chosenEnding), chosenEncoding, chosenEnding, overwrite);
        }, cancel);

    public Task<Result<EditAnswer>> EditAsync(IReadOnlyList<FileEdits> batch, bool dryRun,
        CancellationToken cancel = default) =>
        // A dry run writes nothing, but it still reads every file in the
        // batch and must not see them half-changed by a concurrent edit.
        Mutating(() => Editor.Apply(Roots, batch, dryRun), cancel);

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
