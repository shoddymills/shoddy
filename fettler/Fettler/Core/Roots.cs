using System.Text;

namespace Fettler.Core;

/// <summary>One root as the caller declared it, before it is opened.</summary>
public sealed record RootDecl(string Name, string Path);

/// <summary>
/// A path that has been proved to lie inside a declared root. Nothing in
/// the core accepts a bare string for a path: an operation takes one of
/// these, which is how R8.2's "checked for every operation" is kept by
/// the type system rather than by remembering.
/// </summary>
public sealed record ContainedPath(string RootName, string Full, string Relative)
{
    /// <summary>How the path is written back to a caller. R9.2 requires
    /// one separator form on output whatever arrived on input, and
    /// forward slash is the one both platforms read.</summary>
    public string Display => Relative.Replace('\\', '/');

    /// <summary>The qualified form, used whenever more than one root is
    /// open so a caller can hand the answer straight back (R4.9).</summary>
    public string Qualified => $"{RootName}:{Display}";
}

/// <summary>
/// The boundary of R8: one or more named trees, and the rule that every
/// path lies inside one of them.
///
/// <para><b>Containment is decided by walking down from the volume
/// (R8.1), not by inspecting the deepest component.</b> Given
/// <c>root/a/b</c> where <c>a</c> is a link out of the tree, <c>b</c> is
/// not itself a link and looks perfectly contained; only resolving
/// <c>a</c> on the way past catches it. Every existing component is
/// resolved to its final target and re-checked, and that includes
/// Windows junctions, which are a second kind of reparse point that
/// build tooling plants routinely (R6.11).</para>
///
/// <para><b>The limit, stated rather than implied (R8.3):</b> this
/// compares paths. It does not defend against another process replacing
/// a component between the check and the operation that follows it.
/// Closing that needs operating-system support this does not have. The
/// boundary is a boundary, not a defence against a hostile process
/// sharing the root.</para>
/// </summary>
public sealed class Roots
{
    /// <summary>Case-insensitive on Windows and macOS, case-sensitive
    /// elsewhere, matching the filesystem being guarded (R8.6). The
    /// wrong choice here is a hole rather than an inconvenience:
    /// <c>ROOT/../root/x</c> compares as outside and is inside.</summary>
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    readonly List<(string Name, string Full)> roots;

    Roots(List<(string, string)> roots) => this.roots = roots;

    /// <summary>The declared names, in the order they were declared. The
    /// first is the default for an unqualified relative path.</summary>
    public IReadOnlyList<string> Names => roots.ConvertAll(r => r.Name);

    /// <summary>True when only one root is open, in which case paths are
    /// written bare and R8.7's qualified form never appears.</summary>
    public bool IsSingle => roots.Count == 1;

    /// <summary>The absolute, link-resolved path of a declared root.</summary>
    public string PathOf(string name)
    {
        foreach ((string n, string full) in roots)
            if (n.Equals(name, PathComparison)) return full;
        throw new ArgumentException($"no root called {name}", nameof(name));
    }

    /// <summary>
    /// Open the declared roots, or fail naming the one that was wrong.
    ///
    /// <para>A root the caller named that does not exist is a startup
    /// failure (R8.5) rather than a directory Fettler creates: a
    /// directory the caller named is one they meant, so a typo is
    /// reported instead of materialised.</para>
    /// </summary>
    public static Result<Roots> Open(IReadOnlyList<RootDecl> declared)
    {
        if (declared.Count == 0)
            return Result<Roots>.Fail(Outcome.Invalid, "no root was declared; at least one is required");

        var opened = new List<(string, string)>();
        foreach (RootDecl d in declared)
        {
            if (!IsValidName(d.Name))
                return Result<Roots>.Fail(Outcome.Invalid,
                    $"root name '{d.Name}' is not usable: use two or more characters, " +
                    "starting with a letter, from letters, digits, dot, underscore and hyphen");

            foreach ((string existing, _) in opened)
                if (existing.Equals(d.Name, PathComparison))
                    return Result<Roots>.Fail(Outcome.Invalid, $"root name '{d.Name}' was declared twice");

            string full;
            try
            {
                full = Path.GetFullPath(d.Path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Result<Roots>.Fail(Outcome.Invalid, $"root '{d.Name}' is not a usable path: {e.Message}", d.Path);
            }

            if (!Directory.Exists(full))
                return Result<Roots>.Fail(Outcome.NotFound,
                    $"root '{d.Name}' does not exist", full);

            // The root itself may be reached through a link. Resolve it
            // once here so every later comparison is against the real
            // tree rather than against a name for it.
            string? resolved = FinalTarget(full);
            opened.Add((d.Name, TrimTrailingSeparator(resolved ?? full)));
        }

        return Result<Roots>.Ok(new Roots(opened));
    }

    /// <summary>
    /// Prove that a caller-supplied path lies inside a root, or refuse.
    ///
    /// <para>R8.4 requires that a probe not be a leak, so a path outside
    /// every root is answered identically whether or not anything is
    /// there. The refusal names no target and reports no existence.</para>
    /// </summary>
    public Result<ContainedPath> Resolve(string given)
    {
        if (string.IsNullOrWhiteSpace(given))
            return Result<ContainedPath>.Fail(Outcome.Invalid, "an empty path is not a path");

        (string? rootName, string rest) = SplitRootPrefix(given);

        if (rootName is null && Path.IsPathRooted(rest))
            return ResolveAbsolute(rest);

        string name = rootName ?? roots[0].Name;
        string root = PathOf(name);

        // An absolute path written after a root prefix is only sensible
        // if it lands in that root; treat it as absolute and check.
        if (Path.IsPathRooted(rest))
        {
            Result<ContainedPath> abs = ResolveAbsolute(rest);
            if (abs.IsOk && !abs.Value.RootName.Equals(name, PathComparison))
                return Outside();
            return abs;
        }

        return Walk(name, root, rest);
    }

    /// <summary>Resolve a path that is already anchored to a volume, by
    /// finding which root contains it. Order matters only for nested
    /// roots, where the longest match is the honest answer.</summary>
    Result<ContainedPath> ResolveAbsolute(string absolute)
    {
        string full;
        try { full = Path.GetFullPath(absolute); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<ContainedPath>.Fail(Outcome.Invalid, $"not a usable path: {e.Message}");
        }

        // Longest match wins, which matters only when one root is nested
        // inside another: the deeper name is the honest answer for a path
        // that lies in both.
        string? bestName = null;
        string? bestRoot = null;
        foreach ((string name, string root) in roots)
            if (IsWithin(root, full) && (bestRoot is null || root.Length > bestRoot.Length))
            {
                bestName = name;
                bestRoot = root;
            }

        if (bestName is null || bestRoot is null) return Outside();

        return Walk(bestName, bestRoot, Relativize(bestRoot, full));
    }

    /// <summary>
    /// The walk of R8.1: down from the root, one component at a time,
    /// resolving every link on the way and re-checking containment after
    /// each step.
    /// </summary>
    Result<ContainedPath> Walk(string rootName, string root, string relative)
    {
        string current = root;

        foreach (string part in relative.Split('/', '\\'))
        {
            if (part.Length == 0 || part == ".") continue;

            if (part == "..")
            {
                string? parent = Path.GetDirectoryName(current);
                if (parent is null) return Outside();
                current = parent;
                if (!IsWithin(root, current)) return Outside();
                continue;
            }

            Result<string> named = CheckComponent(part);
            if (!named.IsOk) return named.Carry<ContainedPath>();

            current = Path.Combine(current, part);

            // Resolve this component if it is a link of any kind, then
            // re-check. A link planted here is exactly what R8.1 exists
            // to catch, and it is caught on the way past rather than at
            // the end.
            LinkState link = Inspect(current);
            if (link.IsLink)
            {
                if (link.Target is null)
                    return Result<ContainedPath>.Fail(Outcome.Refused,
                        "a link on this path cannot be resolved, so it cannot be shown to stay inside the root",
                        current);

                current = link.Target;
                if (!IsWithin(root, current)) return Outside();
            }
        }

        return Result<ContainedPath>.Ok(new ContainedPath(rootName, current, Relativize(root, current)));
    }

    static Result<ContainedPath> Outside() =>
        // One message, whatever is or is not there (R8.4).
        Result<ContainedPath>.Fail(Outcome.OutsideRoot,
            "the path is outside every declared root; ask for the roots to see the boundary");

    // ---- names and components ----

    static bool IsValidName(string name) =>
        // Two characters minimum, so a Windows drive letter can never be
        // mistaken for a root prefix: 'C:' is a volume and 'repo:' is a
        // root, and nothing has to guess which was meant.
        name.Length >= 2
        && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    (string? Name, string Remainder) SplitRootPrefix(string given)
    {
        int colon = given.IndexOf(':');
        if (colon <= 0) return (null, given);

        string candidate = given[..colon];
        foreach ((string name, _) in roots)
            if (name.Equals(candidate, PathComparison))
                return (name, given[(colon + 1)..]);

        return (null, given);
    }

    /// <summary>
    /// Names that are legal on macOS and uncreatable on Windows, refused
    /// with an explanation rather than met as an obscure OS error (R9.2).
    /// </summary>
    static readonly string[] ReservedOnWindows =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    static Result<string> CheckComponent(string part)
    {
        if (!OperatingSystem.IsWindows()) return Result<string>.Ok(part);

        if (part.EndsWith('.') || part.EndsWith(' '))
            return Result<string>.Fail(Outcome.Invalid,
                $"'{part}' ends with a dot or a space, which Windows cannot create", part);

        string stem = part.Split('.')[0];
        foreach (string reserved in ReservedOnWindows)
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                return Result<string>.Fail(Outcome.Invalid,
                    $"'{part}' uses the Windows device name {reserved}, which cannot be a filename", part);

        return Result<string>.Ok(part);
    }

    // ---- links ----

    readonly record struct LinkState(bool IsLink, string? Target);

    /// <summary>
    /// Whether a path is a link, and where it finally points.
    ///
    /// <para>Both a symbolic link and a Windows junction answer here:
    /// they are both reparse points, and a boundary that resolves only
    /// one has a hole exactly where a tree is most likely to have one
    /// (R6.11). A link whose target cannot be resolved reports as a link
    /// with no target, and the caller refuses - a dangling link is not
    /// proof of anything, and treating it as an ordinary name would let
    /// a write follow it out of the root.</para>
    /// </summary>
    static LinkState Inspect(string path)
    {
        string? immediate;
        try
        {
            // LinkTarget rather than ResolveLinkTarget, because it
            // answers for a DANGLING link too - one whose entry exists
            // and whose target does not. Exists() follows the link and
            // says no for those, which would let a dangling link out of
            // the root by looking like an ordinary name.
            immediate = new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new LinkState(false, null);
        }

        // A path that is not there is not a link, and must not be treated
        // as one: every create, every write and every move names a
        // destination that does not exist yet.
        if (immediate is null) return new LinkState(false, null);

        return new LinkState(true, FinalTarget(path));
    }

    static string? FinalTarget(string path)
    {
        try
        {
            FileSystemInfo? final =
                File.ResolveLinkTarget(path, returnFinalTarget: true)
                ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return final?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---- comparison ----

    /// <summary>
    /// Whether one path lies inside another, compared the way the
    /// filesystem would (R8.6) and only at a separator boundary, so
    /// <c>/rootless</c> is not inside <c>/root</c>.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        string r = Normalize(TrimTrailingSeparator(root));
        string c = Normalize(TrimTrailingSeparator(candidate));

        if (c.Equals(r, PathComparison)) return true;

        return c.Length > r.Length
            && c.StartsWith(r, PathComparison)
            && (c[r.Length] == Path.DirectorySeparatorChar || c[r.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Composed form for comparison only, never for the path handed back.
    ///
    /// <para>macOS normalizes filenames toward decomposed form and
    /// Windows returns what was written, so a name the caller typed and
    /// the same name read back off the disk can differ byte for byte
    /// while naming one file (R9.2). Comparing without this makes a
    /// contained path look outside its own root.</para>
    /// </summary>
    static string Normalize(string s) =>
        s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);

    static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1) return path;
        // A volume root keeps its separator: C:\ and / are not C: and "".
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    static string Relativize(string root, string full)
    {
        if (full.Equals(root, PathComparison)) return string.Empty;
        string rel = Path.GetRelativePath(root, full);
        return rel == "." ? string.Empty : rel;
    }
}
