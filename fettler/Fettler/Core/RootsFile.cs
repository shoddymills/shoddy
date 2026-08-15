using System.Text.Json;

namespace Fettler.Core;

/// <summary>
/// Where the roots come from when the command line does not say them.
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
/// relative root read against the caller's cwd names a different tree
/// from every directory, which is precisely the fault a wrapper
/// computing absolute paths was invented to work around; resolving
/// against the file's own directory makes the boundary a property of
/// the tree, so the same command means the same thing from anywhere
/// inside it. An absolute path in the file stays absolute.</para>
///
/// <para><b>A malformed file is a refusal, never a fallback.</b>
/// Quietly reverting to the current directory would move the boundary
/// without saying so, and usually widen it - a cwd above the intended
/// root contains the intended root - which is the one direction a
/// containment mistake must never fail in.</para>
/// </summary>
public static class RootsFile
{
    public const string FileName = ".fettler.json";

    /// <summary>A configuration that was found and read: where it was,
    /// and what it declared.</summary>
    public sealed record Found(string File, IReadOnlyList<RootDecl> Roots);

    /// <summary>
    /// The nearest configuration at or above a directory.
    ///
    /// <para>Walking up is what lets a command work from a subdirectory
    /// of the tree rather than only from its top, which is where a
    /// caller usually is. NotFound means the walk reached the volume
    /// without finding one and the caller should fall back; every other
    /// failure means a file WAS found and was wrong, and must be
    /// reported rather than walked past.</para>
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
            if (File.Exists(candidate)) return Read(candidate);
            dir = Path.GetDirectoryName(dir);
        }

        return Result<Found>.Fail(Outcome.NotFound,
            $"no {FileName} at or above the current directory");
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
                    "must hold a JSON object with a \"roots\" object in it", full);

            if (!doc.RootElement.TryGetProperty("roots", out JsonElement roots)
                || roots.ValueKind != JsonValueKind.Object)
                return Result<Found>.Fail(Outcome.Invalid,
                    "needs a \"roots\" object, each entry a name and the path it stands for", full);

            string here = Path.GetDirectoryName(full)!;
            var decls = new List<RootDecl>();

            foreach (JsonProperty entry in roots.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                    return Result<Found>.Fail(Outcome.Invalid,
                        $"root '{entry.Name}' must be a path written as a string", full);

                string raw = entry.Value.GetString() ?? string.Empty;
                if (raw.Length == 0)
                    return Result<Found>.Fail(Outcome.Invalid, $"root '{entry.Name}' has no path", full);

                // Combine leaves an absolute path alone, so a caller who
                // means one may still write one.
                try
                {
                    decls.Add(new RootDecl(entry.Name, Path.GetFullPath(Path.Combine(here, raw))));
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return Result<Found>.Fail(Outcome.Invalid,
                        $"root '{entry.Name}' is not a usable path: {e.Message}", full);
                }
            }

            if (decls.Count == 0)
                return Result<Found>.Fail(Outcome.Invalid, "declares no roots", full);

            return Result<Found>.Ok(new Found(full, decls));
        }
    }
}
