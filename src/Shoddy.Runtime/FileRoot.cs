// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

/// <summary>
/// The directory a run's file words may not reach outside of.
///
/// WHY THIS EXISTS. A host that embeds Shoddy hands it a root and
/// expects the mill's files to live there. Before this, the root was
/// only VALIDATED — it had to exist — and then disciplined nothing: the
/// runtime resolved every relative path against the process working
/// directory, so `"../../elsewhere.png" PLOTSAVE` and
/// `"../out.tape" WRITELINES` both wrote wherever they liked. Setting
/// the working directory to the root does not help, because it fixes
/// where a path STARTS and not where it may END.
///
/// OFF BY DEFAULT, AND THAT IS DELIBERATE. With no root set, Contain
/// hands the path back untouched and the runtime behaves exactly as it
/// always has. `mill run` is a general-purpose tool run by someone who
/// could open the file with an editor anyway, and confining it to the
/// current directory would break every program that reads from
/// somewhere else. The root is a HOST's tool: a host embedding Shoddy
/// in an application has a boundary to keep, and this is how it keeps
/// it.
///
/// THE SWITCH IS AMBIENT, in the same shape as the network's, and for
/// the same reason: a woven Mode T program constructs its own engine
/// inside its preamble, so a switch read from the environment at
/// construction is the only one that reaches both modes.
///
/// WHAT IT DEFENDS AGAINST, precisely. A path that climbs out with
/// `..`, an absolute path to anywhere else, and a symbolic link
/// anywhere along the way — every existing component is resolved to its
/// final target before the comparison, so a link planted mid-path
/// cannot smuggle the destination out. What it does NOT defend against
/// is another process replacing a component with a link BETWEEN this
/// check and the open that follows it. Closing that needs the operating
/// system (openat2, O_NOFOLLOW) rather than a path comparison, and it
/// is a different piece of work; the check here is a boundary, not a
/// defence against a hostile process sharing the root.
/// </summary>
public static class FileRoot
{
    /// <summary>The environment variable a host sets, read once at
    /// engine construction. Named like SHODDY_ALLOW_NET because it is
    /// the same kind of thing: policy fixed for the life of a run.</summary>
    public const string Variable = "SHODDY_FILE_ROOT";

    static readonly char[] Separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    static readonly StringComparison Compare =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>A root as the engine will hold it: absolute, with links
    /// resolved and any trailing separator off, or null when there is
    /// none. A root that is itself a link is resolved here, once, so
    /// every later comparison is against the real place.</summary>
    public static string? Of(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Real(Path.GetFullPath(raw)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable root is not a root. Treating it as absent
            // would silently drop the boundary, so it is treated as one
            // nothing can satisfy instead — see Contain.
            return raw;
        }
    }

    /// <summary>The path the runtime should actually use, or null when
    /// it lies outside the root.
    ///
    /// With no root, the path is handed straight back and nothing
    /// changes. With one, a RELATIVE path is resolved against the ROOT
    /// rather than against the process working directory — so a host no
    /// longer has to chdir for the mill's own files to land in the
    /// right place, and one that does chdir gets the same answer.</summary>
    public static string? Contain(string? root, string path)
    {
        if (root is null) return path;
        if (string.IsNullOrEmpty(path)) return null;

        string full;
        try { full = Path.GetFullPath(path, root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string real = Path.TrimEndingDirectorySeparator(Real(full));
        if (real.Equals(root, Compare)) return full;
        if (real.StartsWith(root, Compare)
            && real.Length > root.Length
            && Separators.Contains(real[root.Length]))
            return full;
        return null;
    }

    /// <summary>Every existing component of a path, from the volume
    /// down, resolved to its final link target.
    ///
    /// Walking DOWN rather than resolving only the deepest existing
    /// ancestor is what closes the mid-path case: given root/a/b where
    /// `a` is a link out of the root and `b` exists through it, `b`
    /// itself is not a link and resolving only `b` would answer a path
    /// that still reads as being inside. Resolving `a` on the way past
    /// it does not.</summary>
    static string Real(string full)
    {
        string cur = Path.GetPathRoot(full) ?? "";
        string rest = cur.Length == 0 ? full : full[cur.Length..];
        foreach (string part in rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur.Length == 0 ? part : Path.Combine(cur, part);
            try
            {
                FileSystemInfo? target =
                    Directory.Exists(cur) ? Directory.ResolveLinkTarget(cur, returnFinalTarget: true)
                  : File.Exists(cur) ? File.ResolveLinkTarget(cur, returnFinalTarget: true)
                  : null;
                if (target != null) cur = target.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not readable, or a link we are not allowed to follow.
                // The string form stands, which can only make the check
                // STRICTER — never looser — because an unresolved link
                // keeps the path where it appeared to be.
            }
        }
        return cur;
    }
}
