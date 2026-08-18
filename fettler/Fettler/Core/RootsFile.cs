using System.Text.Json;

namespace Fettler.Core;

/// <summary>
/// Where the trees come from when the command line does not say them,
/// and the only place a permission beyond <c>list read</c> can be
/// granted.
///
/// <para>A caller working in one tree should not have to spell that
/// tree's boundary out on every command. The answer that suggests
/// itself - a shell script per platform holding the paths and passing
/// them through - is worse than the problem it solves: it puts a shell
/// back between the caller and the files, which is the thing this
/// program exists to remove, and being a twin pair it has to be kept in
/// step by hand. This file is that job done once, in the program, and
/// both front ends get it because both reach the boundary through
/// <see cref="Fettler.Cli.Arguments.DeclaredRoots"/>.</para>
///
/// <para><b>A path in the file is relative to THE FILE, never to the
/// current directory (R8.9).</b> That is the whole point of it. A
/// relative tree read against the caller's cwd names a different tree
/// from every directory, which is precisely the fault a wrapper
/// computing absolute paths was invented to work around; resolving
/// against the file's own directory makes the boundary a property of
/// the tree, so the same command means the same thing from anywhere
/// inside it. An absolute path in the file stays absolute.</para>
///
/// <para><b>A malformed file is a refusal, never a fallback.</b>
/// Quietly reverting to the current directory would move the boundary
/// without saying so, and usually widen it - a cwd above the intended
/// tree contains the intended tree - which is the one direction a
/// containment mistake must never fail in.</para>
///
/// <para><b>Writing one is the deliberate act that grants write access.</b>
/// A command line cannot: <c>--root</c> grants <c>list read</c> and
/// nothing more. To write to a tree you put a file in it, which is
/// reviewable, sits beside what it governs, and is something a person
/// did rather than something a caller asked for.</para>
/// </summary>
public static class RootsFile
{
    public const string FileName = ".fettler.json";

    /// <summary>
    /// The personal overlay (B.12): gitignored, merged over the
    /// checked-in file.
    ///
    /// <para>The checked-in file can only name trees that ship with the
    /// tree itself, because a tree that does not exist is a startup
    /// failure. So without this there is no way to say "and my
    /// requirements checkout, and my assistant's scratch folder" - and
    /// <c>--root</c> REPLACES rather than adds, so naming one sibling
    /// silently drops the main tree. Merging is right here and wrong
    /// there: this is one tree's configuration layered on itself, not a
    /// caller overriding a tree.</para>
    /// </summary>
    public const string LocalFileName = ".fettler.local.json";

    /// <summary>A configuration that was found and read: where it was,
    /// and what it declared.</summary>
    public sealed record Found(string File, IReadOnlyList<TreeDecl> Trees)
    {
        /// <summary>Where the boundary came from, in words, including the
        /// overlay when there was one.</summary>
        public string Origin => Overlay is null ? File : $"{File} (with {Overlay})";

        public string? Overlay { get; init; }

        /// <summary>The tasks it declared, empty when it declared none.
        /// What may run in a tree and what may be done to it are one
        /// statement about one tree, so one file states both.</summary>
        public IReadOnlyList<TaskDecl> Tasks { get; init; } = [];
    }

    /// <summary>
    /// The nearest configuration at or above a directory, with its
    /// overlay merged over it.
    ///
    /// <para>Walking up is what lets a command work from a subdirectory
    /// of the tree rather than only from its top, which is where a
    /// caller usually is. NotFound means the walk reached the volume
    /// without finding one; every other failure means a file WAS found
    /// and was wrong, and must be reported rather than walked past.</para>
    /// </summary>
    public static Result<Found> Discover(string from)
    {
        string? dir;
        try
        {
            dir = Path.GetFullPath(from);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<Found>.Fail(Outcome.Invalid, $"cannot read the directory to search from: {e.Message}", from);
        }

        while (dir is not null)
        {
            string candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate)) return ReadWithOverlay(candidate);
            dir = Path.GetDirectoryName(dir);
        }

        return Result<Found>.Fail(Outcome.NotFound,
            $"no {FileName} at or above the current directory");
    }

    /// <summary>Read a configuration and the personal overlay beside it,
    /// if there is one.</summary>
    public static Result<Found> ReadWithOverlay(string file)
    {
        Result<Found> found = Read(file);
        if (!found.IsOk) return found;

        string overlay = Path.Combine(Path.GetDirectoryName(found.Value.File)!, LocalFileName);
        if (!File.Exists(overlay)) return found;

        Result<Found> local = Read(overlay);
        if (!local.IsOk) return local;

        return Result<Found>.Ok(new Found(found.Value.File, Merge(found.Value.Trees, local.Value.Trees))
        {
            Overlay = local.Value.File,
            Tasks = MergeTasks(found.Value.Tasks, local.Value.Tasks),
        });
    }

    /// <summary>
    /// The boundary, re-read from a named configuration and opened.
    ///
    /// <para><b>This is serve's per-request read.</b> The CLI reads the
    /// file fresh on every command because every command is a fresh
    /// process; a server that read it once would hold a boundary the
    /// file no longer states, and stay wider than a person's revocation
    /// until somebody thought to restart it - the one direction a
    /// boundary must never fail in. What is re-read is the FILE FOUND AT
    /// LAUNCH, never a fresh discovery: a configuration that has gone
    /// missing refuses, because walking up from where it was could find
    /// a broader one above it.</para>
    /// </summary>
    public static Result<Roots> Reopen(string file)
    {
        Result<Found> found = ReadWithOverlay(file);
        return found.IsOk
            ? Roots.Open(found.Value.Trees, found.Value.Origin, found.Value.Tasks)
            : found.Carry<Roots>();
    }

    /// <summary>
    /// The overlay's trees over the checked-in ones: a name in both is
    /// the overlay's ENTIRELY, and a name only the overlay has is added.
    ///
    /// <para>Replacing a whole tree rather than merging its fields is the
    /// predictable rule. Field-level merging would let a local file grant
    /// a permission by mentioning one key while appearing to say nothing
    /// about the rest, and nobody reading the two files side by side
    /// could say what the result was without running it.</para>
    /// </summary>
    static IReadOnlyList<TreeDecl> Merge(IReadOnlyList<TreeDecl> shipped, IReadOnlyList<TreeDecl> local)
    {
        var merged = new List<TreeDecl>();

        foreach (TreeDecl tree in shipped)
        {
            TreeDecl? replacement = null;
            foreach (TreeDecl l in local)
                if (l.Name.Equals(tree.Name, Roots.PathComparison)) replacement = l;
            merged.Add(replacement ?? tree);
        }

        foreach (TreeDecl l in local)
        {
            bool already = false;
            foreach (TreeDecl m in merged)
                if (m.Name.Equals(l.Name, Roots.PathComparison)) already = true;
            if (!already) merged.Add(l);
        }

        return merged;
    }

    /// <summary>Read one named configuration, failing with what was
    /// wrong with it rather than with a parser's word for it.</summary>
    public static Result<Found> Read(string file)
    {
        string full;
        try
        {
            full = Path.GetFullPath(file);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<Found>.Fail(Outcome.Invalid, $"not a usable path: {e.Message}", file);
        }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result<Found>.Fail(Outcome.NotFound, "there is no configuration there", full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Found>.Fail(Outcome.Denied, $"the configuration could not be read: {e.Message}", full);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            return Result<Found>.Fail(Outcome.Invalid, $"is not valid JSON: {e.Message}", full);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Result<Found>.Fail(Outcome.Invalid,
                    "must hold a JSON object with a \"trees\" object in it", full);

            // The old shape meant something different: a root was
            // readable and writable in full, and there was no way to say
            // otherwise. Silently reading it as the new shape would
            // GRANT more than it says, which is the one direction this
            // must never fail in - so it is refused with the migration
            // named rather than accepted.
            if (doc.RootElement.TryGetProperty("roots", out _))
                return Result<Found>.Fail(Outcome.Invalid, Migration, full);

            if (!doc.RootElement.TryGetProperty("trees", out JsonElement trees)
                || trees.ValueKind != JsonValueKind.Object)
                return Result<Found>.Fail(Outcome.Invalid,
                    "needs a \"trees\" object, each entry a name and an object with a \"path\" in it", full);

            string here = Path.GetDirectoryName(full)!;
            var decls = new List<TreeDecl>();

            foreach (JsonProperty entry in trees.EnumerateObject())
            {
                Result<TreeDecl> one = ReadTree(entry, here, full);
                if (!one.IsOk) return one.Carry<Found>();
                decls.Add(one.Value);
            }

            if (decls.Count == 0)
                return Result<Found>.Fail(Outcome.Invalid, "declares no trees", full);

            Result<IReadOnlyList<TaskDecl>> tasks = ReadTasks(doc.RootElement, full);
            if (!tasks.IsOk) return tasks.Carry<Found>();

            return Result<Found>.Ok(new Found(full, decls) { Tasks = tasks.Value });
        }
    }

    /// <summary>The one message a caller with the old shape sees, so it
    /// says the whole of what to do rather than that something is
    /// wrong.</summary>
    public const string Migration =
        "uses \"roots\", which this version does not read. A root granted "
        + "everything; a tree states what it grants. Rewrite "
        + "{\"roots\":{\"work\":\".\"}} as "
        + "{\"trees\":{\"work\":{\"path\":\".\"}}} - a tree whose path is the "
        + "folder this file sits in gets list read create update rename delete, "
        + "and every other tree gets list read unless \"can\" says more. "
        + "execute is never granted by default.";

    static Result<TreeDecl> ReadTree(JsonProperty entry, string here, string file)
    {
        if (entry.Value.ValueKind == JsonValueKind.String)
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' is written as a bare path; it needs an object, "
                + $"as {{\"{entry.Name}\":{{\"path\":\"{entry.Value.GetString()}\"}}}}", file);

        if (entry.Value.ValueKind != JsonValueKind.Object)
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' must be an object with a \"path\" in it", file);

        if (!entry.Value.TryGetProperty("path", out JsonElement pathValue)
            || pathValue.ValueKind != JsonValueKind.String
            || pathValue.GetString() is not { Length: > 0 } raw)
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' needs a \"path\" written as a non-empty string", file);

        if (Separator(raw, $"tree '{entry.Name}' has a \"path\" that", file) is { } badPath)
            return Result<TreeDecl>.Fail(badPath);

        string path;
        try
        {
            // Combine leaves an absolute path alone, so a caller who
            // means one may still write one.
            path = Path.GetFullPath(Path.Combine(here, raw));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' is not a usable path: {e.Message}", file);
        }

        // The default depends on WHERE the tree is, not on how it was
        // spelled: the folder this configuration sits in is the one the
        // author is working in, and everything else is somebody else's.
        bool isOwnFolder = Roots.IsWithin(path, here) && Roots.IsWithin(here, path);
        Permission can = isOwnFolder ? Permissions.Full : Permissions.ReadOnly;

        if (entry.Value.TryGetProperty("can", out JsonElement canValue))
        {
            Result<Permission> parsed = ReadCan(canValue, $"tree '{entry.Name}'", file);
            if (!parsed.IsOk) return parsed.Carry<TreeDecl>();
            can = parsed.Value;
        }

        var scopes = new List<ScopeDecl>();
        if (entry.Value.TryGetProperty("scopes", out JsonElement scopeValue))
        {
            if (scopeValue.ValueKind != JsonValueKind.Object)
                return Result<TreeDecl>.Fail(Outcome.Invalid,
                    $"tree '{entry.Name}' has \"scopes\" that is not an object of path to permissions", file);

            foreach (JsonProperty scope in scopeValue.EnumerateObject())
            {
                if (scope.Value.ValueKind != JsonValueKind.Object
                    || !scope.Value.TryGetProperty("can", out JsonElement scopeCan))
                    return Result<TreeDecl>.Fail(Outcome.Invalid,
                        $"scope '{scope.Name}' in tree '{entry.Name}' needs a \"can\" array, "
                        + "which may be empty to hide it", file);

                Result<Permission> parsed = ReadCan(scopeCan, $"scope '{scope.Name}'", file);
                if (!parsed.IsOk) return parsed.Carry<TreeDecl>();

                if (Separator(scope.Name, $"scope '{scope.Name}' in tree '{entry.Name}'", file) is { } badScope)
                    return Result<TreeDecl>.Fail(badScope);

                scopes.Add(new ScopeDecl(scope.Name, parsed.Value));
            }
        }

        return Result<TreeDecl>.Ok(new TreeDecl(entry.Name, path, can, scopes));
    }

    /// <summary>
    /// The declared tasks of R7, read from the same file that declares the
    /// trees.
    ///
    /// <para><b><c>run</c> is the command line, written as one string.</b>
    /// It is split once, by <see cref="Tasks.Split"/>, whose grammar is
    /// whitespace and double quotes and nothing else - no variables, no
    /// globbing, no operators, and no backslash escapes, so a Windows path
    /// is written as it is. The program is then launched with the resulting
    /// list, never with a command line for a shell to re-split.</para>
    ///
    /// <para><b>The file is trusted input, and R7.6 says so out loud.</b>
    /// Containment guards paths; it does not and cannot guard a command
    /// list. Whoever can write this file chooses what a caller - a model
    /// included - is able to execute, which is why writing it is a
    /// person's act with an editor and why <c>execute</c> is never a
    /// default. Point Fettler at a tree you trust.</para>
    /// </summary>
    static Result<IReadOnlyList<TaskDecl>> ReadTasks(JsonElement root, string file)
    {
        if (!root.TryGetProperty("tasks", out JsonElement tasks))
            return Result<IReadOnlyList<TaskDecl>>.Ok([]);

        if (tasks.ValueKind != JsonValueKind.Object)
            return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                "has a \"tasks\" that is not an object of name to declaration", file);

        var declared = new List<TaskDecl>();
        foreach (JsonProperty entry in tasks.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}' needs an object with a \"run\" string in it, as "
                    + $"{{\"{entry.Name}\":{{\"run\":\"pwsh -File build.ps1\"}}}}", file);

            if (!entry.Value.TryGetProperty("run", out JsonElement run)
                || run.ValueKind != JsonValueKind.String
                || run.GetString() is not { } line)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}' needs a \"run\" written as one string, the command line", file);

            Result<IReadOnlyList<string>> split = Tasks.Split(line);
            if (!split.IsOk)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}': {split.Failure!.Message}", file);

            IReadOnlyList<string> command = split.Value;
            if (command.Count == 0)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}' has an empty \"run\"; its first word is the program", file);

            string? cwd = null;
            if (entry.Value.TryGetProperty("cwd", out JsonElement where))
            {
                if (where.ValueKind != JsonValueKind.String
                    || where.GetString() is not { Length: > 0 } named)
                    return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                        $"task '{entry.Name}' has a \"cwd\" that is not a non-empty string", file);

                if (Separator(named, $"task '{entry.Name}' has a \"cwd\" that", file) is { } bad)
                    return Result<IReadOnlyList<TaskDecl>>.Fail(bad);

                cwd = named;
            }

            foreach (TaskDecl already in declared)
                if (already.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                    return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                        $"task '{entry.Name}' is declared twice", file);

            declared.Add(new TaskDecl(entry.Name, command, cwd, line));
        }

        return Result<IReadOnlyList<TaskDecl>>.Ok(declared);
    }

    /// <summary>
    /// A declared path is written with forward slashes, on every platform.
    ///
    /// <para><b>There is no formal standard for this - there is a
    /// convention, and it is git's:</b> relative, forward-slashed, no drive
    /// letter. <c>.gitignore</c>, Docker, npm and VS Code's own task files
    /// all use it, and it is the only spelling that means one thing
    /// everywhere. Windows accepts <c>/</c> in every API; on macOS and
    /// Linux a backslash is an ordinary character in a file name rather
    /// than a separator, so a checked-in <c>..\tree</c> does not name a
    /// parent there - it names a file with a backslash in it, and finds
    /// nothing.</para>
    ///
    /// <para><b>Refused rather than converted</b>, because converting would
    /// make a file legitimately called <c>a\b</c> unreachable on the
    /// platforms where that is a legal name.</para>
    ///
    /// <para><b>Only a RELATIVE path is held to it.</b> A relative path is
    /// the one that travels - it is the one worth checking in, and the one
    /// that has to mean the same thing on the next machine. An absolute
    /// path names a particular disk and cannot travel whatever its
    /// separators are, so <c>C:\work\tree</c> in a gitignored
    /// <c>.fettler.local.json</c> is a person describing their own machine
    /// and is left alone.</para>
    /// </summary>
    static Failure? Separator(string raw, string what, string file) =>
        raw.Contains('\\') && !Absolute(raw)
            ? new Failure(Outcome.Invalid,
                $"{what} is written with a backslash. A relative declared path uses \"/\" on "
                + "every platform: Windows accepts it, and on macOS and Linux a backslash is "
                + "an ordinary character in a name rather than a separator", file)
            : null;

    /// <summary>Whether a declared path names a particular disk. A drive
    /// letter is recognised on every platform, because the question being
    /// asked is what the author MEANT, and they meant an absolute Windows
    /// path even when this is read on Linux.</summary>
    static bool Absolute(string raw) =>
        Path.IsPathRooted(raw)
        || (raw.Length > 1 && raw[1] == ':' && char.IsLetter(raw[0]))
        || raw.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>The overlay's tasks over the checked-in ones, by the same
    /// rule the trees use: a name in both is the overlay's entirely, and a
    /// name only the overlay has is added. Replacing a whole task rather
    /// than merging its fields keeps two files readable side by side.</summary>
    static IReadOnlyList<TaskDecl> MergeTasks(
        IReadOnlyList<TaskDecl> shipped, IReadOnlyList<TaskDecl> local)
    {
        var merged = new List<TaskDecl>();

        foreach (TaskDecl task in shipped)
        {
            TaskDecl? replacement = null;
            foreach (TaskDecl l in local)
                if (l.Name.Equals(task.Name, StringComparison.OrdinalIgnoreCase)) replacement = l;
            merged.Add(replacement ?? task);
        }

        foreach (TaskDecl l in local)
        {
            bool already = false;
            foreach (TaskDecl m in merged)
                if (m.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase)) already = true;
            if (!already) merged.Add(l);
        }

        return merged;
    }

    static Result<Permission> ReadCan(JsonElement value, string what, string file)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return Result<Permission>.Fail(Outcome.Invalid,
                $"{what} has a \"can\" that is not an array of permission words", file);

        var words = new List<string>();
        foreach (JsonElement word in value.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.String)
                return Result<Permission>.Fail(Outcome.Invalid,
                    $"{what} lists a permission that is not a word", file);
            words.Add(word.GetString()!);
        }

        Result<Permission> parsed = Permissions.Parse(words);
        if (!parsed.IsOk)
            return Result<Permission>.Fail(Outcome.Invalid, $"{what}: {parsed.Failure!.Message}", file);

        return parsed;
    }
}
