using Fettler.Core;

namespace Fettler.Cli;

/// <summary>
/// A parsed command line: the verb, its positional arguments, and its
/// flags. Deliberately small - there is no options library here and no
/// need for one, because R1.2 forbids a dependency and the surface is a
/// verb, some paths and some flags.
///
/// <para>Flags are <c>--name</c> or <c>--name value</c>, and repeat where
/// the verb says they may (<c>--root</c>, <c>--exclude</c>, <c>-e</c>).
/// Anything after a bare <c>--</c> is positional whatever it looks like,
/// which is how a path beginning with a hyphen gets through.</para>
/// </summary>
public sealed class Arguments
{
    readonly Dictionary<string, List<string>> flags = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> positional = [];

    public string Verb { get; private set; } = string.Empty;

    public IReadOnlyList<string> Positional => positional;

    /// <summary>Flags that take no value, so the parser does not eat the
    /// next argument as one. Everything else takes a value.</summary>
    static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "overwrite", "recursive", "force", "dry-run", "stdin", "literal",
        "case-sensitive", "include-generated", "count", "files-only", "sort-modified",
        "preserve-times", "all", "on", "off", "help", "version",

        // R5.11: each names a stream rather than a value, so the parser
        // must not eat the following argument as one.
        "replace-stdin", "with-stdin", "text-stdin",

        // R8.9: names no value - it turns the search for a .fettler.json
        // off rather than pointing at a different one.
        "no-config",

        // R4.20: the search pattern, arriving as bytes rather than as an
        // argument some shell has already had an opinion about.
        "pattern-stdin",
    };

    /// <summary>
    /// Every flag any verb accepts, in one set, so that a flag no verb
    /// knows can be REFUSED rather than ignored (R3.14).
    ///
    /// <para>Ignoring one is not a small discourtesy. A flag that takes a
    /// value also eats the argument after it, so a misspelt
    /// <c>--gob PATTERN</c> silently swallows the pattern and the command
    /// then searches the whole tree for something else and reports the
    /// answer with complete confidence. A typo has to fail, because the
    /// alternative is not a failure but a wrong answer.</para>
    ///
    /// <para><b>The limit, stated rather than implied:</b> this is one
    /// set across all verbs, so it catches a misspelling and not a flag
    /// handed to the wrong verb - <c>read --overwrite</c> still passes.
    /// A table per verb would catch both and would have to be kept in
    /// step with the dispatcher by hand; one list that cannot drift from
    /// itself is worth more than a second list that can.</para>
    /// </summary>
    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // the boundary, and the shape of the answer
        "root", "config", "no-config", "json", "include-generated", "exclude",
        "help", "version",

        // find
        "sort", "sort-modified", "since", "limit",

        // search
        "pattern", "pattern-file", "pattern-stdin", "glob", "in", "literal",
        "case-sensitive", "context", "count", "files-only",

        // read
        "from", "to",

        // write
        "stdin", "overwrite", "encoding", "eol",

        // edit, and the R5.11 spellings of the text it is given
        "expect", "dry-run", "between", "all", "insert-after", "delete",
        "script", "script-inline",
        "replace", "replace-file", "replace-stdin",
        "with", "with-file", "with-stdin",
        "text", "text-file", "text-stdin",

        // move, copy, delete, exec, run
        "recursive", "preserve-times", "force", "on", "off", "timeout",
    };

    /// <summary>Whether any verb accepts this flag. Public so a test can
    /// hold the help text and this set against each other: the help is
    /// the only place a caller learns the names, and a name that appears
    /// there and is refused here is worse than either alone.</summary>
    public static bool IsKnown(string flag) => Known.Contains(flag);

    /// <summary>Flags that were given and that no verb knows.</summary>
    public IReadOnlyList<string> UnknownFlags
    {
        get
        {
            var unknown = new List<string>();
            foreach (string name in flags.Keys)
                if (!Known.Contains(name)) unknown.Add(name);
            return unknown;
        }
    }

    public static Arguments Parse(IReadOnlyList<string> argv)
    {
        var parsed = new Arguments();
        bool literalFromHere = false;

        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];

            if (!literalFromHere && a == "--") { literalFromHere = true; continue; }

            if (!literalFromHere && a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                string name = a[2..];
                string? inline = null;

                int equals = name.IndexOf('=');
                if (equals > 0) { inline = name[(equals + 1)..]; name = name[..equals]; }

                if (Switches.Contains(name) && inline is null) { parsed.Add(name, "true"); continue; }

                if (inline is not null) { parsed.Add(name, inline); continue; }
                if (i + 1 < argv.Count) { parsed.Add(name, argv[++i]); continue; }

                parsed.Add(name, "true");
                continue;
            }

            // -e is the one short flag, and it is short because a search
            // carrying several patterns is a thing typed by hand.
            if (!literalFromHere && a == "-e" && i + 1 < argv.Count)
            {
                parsed.Add("pattern", argv[++i]);
                continue;
            }

            if (parsed.Verb.Length == 0 && !literalFromHere) parsed.Verb = a;
            else parsed.positional.Add(a);
        }

        return parsed;
    }

    void Add(string name, string value)
    {
        if (!flags.TryGetValue(name, out List<string>? values)) flags[name] = values = [];
        values.Add(value);
    }

    public bool Has(string name) => flags.ContainsKey(name);

    public string? Value(string name) =>
        flags.TryGetValue(name, out List<string>? v) && v.Count > 0 ? v[^1] : null;

    public IReadOnlyList<string> Values(string name) =>
        flags.TryGetValue(name, out List<string>? v) ? v : [];

    public int Int(string name, int fallback) =>
        int.TryParse(Value(name), out int n) ? n : fallback;

    public string? At(int index) => index < positional.Count ? positional[index] : null;

    /// <summary>
    /// The roots of R8.7, and where they come from when the command line
    /// does not say (R8.9).
    ///
    /// <para>Declared as <c>--root NAME=PATH</c>, repeatable. A bare
    /// <c>--root PATH</c> still works and is named <c>root</c>, so the
    /// single-root form stays exactly as short as it was and nothing
    /// about an existing caller changes.</para>
    ///
    /// <para><b>The command line REPLACES the file rather than adding to
    /// it.</b> Merging would leave a caller unable to narrow the
    /// boundary: whatever they asked for, the tree's own configuration
    /// would still be open beside it, and "just this directory, please"
    /// would be unsayable.</para>
    ///
    /// <para>Finding no configuration is not a failure - the current
    /// directory is a perfectly good root, and it is what a caller
    /// outside any configured tree means. Finding a BROKEN one is, and
    /// stops the run rather than quietly widening the boundary to the
    /// cwd.</para>
    /// </summary>
    public Result<Roots> DeclaredRoots()
    {
        IReadOnlyList<string> declared = Values("root");
        if (declared.Count > 0)
        {
            var decls = new List<RootDecl>();
            foreach (string entry in declared)
            {
                int equals = entry.IndexOf('=');

                // A bare Windows path has a colon in it and no equals, so
                // the split is on equals alone and never on the colon that
                // makes C:\ a volume.
                if (equals <= 0) decls.Add(new RootDecl(decls.Count == 0 ? "root" : $"root{decls.Count}", entry));
                else decls.Add(new RootDecl(entry[..equals], entry[(equals + 1)..]));
            }

            return Roots.Open(decls);
        }

        if (Value("config") is { } named)
        {
            Result<RootsFile.Found> told = RootsFile.Read(named);
            return told.IsOk
                ? Roots.Open(told.Value.Roots, told.Value.File)
                : told.Carry<Roots>();
        }

        if (!Has("no-config"))
        {
            Result<RootsFile.Found> found = RootsFile.Discover(Directory.GetCurrentDirectory());
            if (found.IsOk) return Roots.Open(found.Value.Roots, found.Value.File);

            // NotFound is the ordinary case and falls through to the cwd;
            // anything else means a configuration WAS there and was wrong.
            if (found.Failure!.Outcome != Outcome.NotFound) return found.Carry<Roots>();
        }

        return Roots.Open(
            [new RootDecl("root", Directory.GetCurrentDirectory())],
            $"the current directory, because no --root and no {RootsFile.FileName}");
    }
}
