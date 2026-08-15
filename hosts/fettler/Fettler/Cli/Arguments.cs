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
    };

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
    /// The roots of R8.7, declared as <c>--root NAME=PATH</c> and
    /// repeatable. A bare <c>--root PATH</c> still works and is named
    /// <c>root</c>, so the single-root form stays exactly as short as it
    /// was and nothing about an existing caller changes.
    /// </summary>
    public Result<Roots> DeclaredRoots()
    {
        IReadOnlyList<string> declared = Values("root");
        if (declared.Count == 0)
            return Roots.Open([new RootDecl("root", Directory.GetCurrentDirectory())]);

        var decls = new List<RootDecl>();
        foreach (string entry in declared)
        {
            int equals = entry.IndexOf('=');

            // A bare Windows path has a colon in it and no equals, so the
            // split is on equals alone and never on the colon that makes
            // C:\ a volume.
            if (equals <= 0) decls.Add(new RootDecl(decls.Count == 0 ? "root" : $"root{decls.Count}", entry));
            else decls.Add(new RootDecl(entry[..equals], entry[(equals + 1)..]));
        }

        return Roots.Open(decls);
    }
}
