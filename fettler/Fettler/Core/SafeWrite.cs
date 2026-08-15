namespace Fettler.Core;

/// <summary>What a write did, including what it could not carry over.</summary>
public sealed record Saved(
    ContainedPath Path,
    long Bytes,
    string EncodingName,
    LineEnding LineEnding,
    bool MixedEndings,
    bool FinalNewline,
    string Hash,
    bool Created,
    IReadOnlyList<string> MetadataLost);

/// <summary>
/// The write of R4.3: interrupted at any point it leaves either the
/// original content or the new content, never a truncated file.
///
/// <para><b>The mechanism is stage-and-replace.</b> Content goes to a
/// temporary file beside the destination - beside, so the replace is a
/// rename within one volume rather than a copy across two - is forced to
/// the disk, and only then takes the destination's name. A crash before
/// the rename leaves the original untouched and a stray temporary; a
/// crash after it leaves the new content whole. There is no moment at
/// which the destination holds half of anything.</para>
///
/// <para><b>And it carries the file's metadata across (R4.18).</b> The
/// replace makes a new file wear an old name, and a new file has none of
/// the old one's attributes, alternate data streams or extended
/// attributes unless they are deliberately moved. Left alone this
/// silently strips a Mark-of-the-Web or a quarantine flag on the way
/// past, which is a security marker weakened as a side effect of editing
/// text. What could not be carried is named in <see cref="Saved"/>
/// rather than swallowed.</para>
/// </summary>
public static class SafeWrite
{
    public static Result<Saved> Text(ContainedPath path, string text, string encodingName,
        LineEnding ending, bool overwrite)
    {
        byte[] bytes = TextIo.Encode(text, encodingName);
        Result<Saved> saved = Bytes(path, bytes, overwrite);
        if (!saved.IsOk) return saved;

        // Defect 1.2: `read` says when a file disagrees with itself about
        // line endings, and `write` said nothing - so a file could be
        // made mixed by a write and only found out about on the next
        // read, or by version control two commits later. The mixture is
        // computed from what was actually written rather than from what
        // was asked for.
        IReadOnlyList<Line> lines = TextIo.SplitLines(text);
        return Result<Saved>.Ok(saved.Value with
        {
            EncodingName = encodingName,
            LineEnding = ending,
            MixedEndings = TextIo.Mixed(lines),
            FinalNewline = lines.Count > 0 && lines[^1].Ending.Length > 0,
        });
    }

    public static Result<Saved> Bytes(ContainedPath path, byte[] content, bool overwrite)
    {
        string full = path.Full;

        if (Directory.Exists(full))
            return Result<Saved>.Fail(Outcome.Refused,
                "a directory is already using that name", path.Display);

        bool exists = File.Exists(full);
        if (exists && !overwrite)
            return Result<Saved>.Fail(Outcome.TargetExists,
                "the file exists; pass overwrite to replace it", path.Display);

        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
            return Result<Saved>.Fail(Outcome.NotFound,
                "the directory to write into does not exist; create it first", path.Display);

        // R6.10: the Windows read-only attribute blocks a replace with an
        // error that names nothing. Read it and say so instead.
        Result<Saved> readOnly = RefuseIfReadOnly<Saved>(full, path);
        if (!readOnly.IsOk) return readOnly;

        FileMetadata carried = Metadata.Capture(full);
        string temp = Path.Combine(parent, $".fettle-{Guid.NewGuid():N}.tmp");

        try
        {
            // WriteThrough plus an explicit flush-to-disk: without it the
            // rename can reach the disk before the bytes do, and a crash
            // in that window leaves the destination naming content that
            // was never written.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, full, exists);
        }
        catch (UnauthorizedAccessException e)
        {
            Discard(temp);
            return Result<Saved>.Fail(Outcome.Denied, TextIo.Denied(path, e), path.Display);
        }
        catch (IOException e)
        {
            Discard(temp);
            return Result<Saved>.Fail(Outcome.Denied, TextIo.Denied(path, e), path.Display);
        }

        IReadOnlyList<string> lost = carried.IsEmpty
            ? []
            : Metadata.Apply(full, carried);

        return Result<Saved>.Ok(new Saved(
            path, content.Length, "utf-8", LineEnding.Lf, MixedEndings: false,
            FinalNewline: false, Hash: TextIo.HashOf(content),
            Created: !exists, MetadataLost: lost));
    }

    static void Replace(string temp, string destination, bool destinationExists)
    {
        if (!destinationExists)
        {
            File.Move(temp, destination);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // ReplaceFile keeps the destination's attributes and ACLs,
                // which is strictly more than a move does. It refuses
                // across volumes and on some network shares, and the move
                // below is the answer when it does.
                File.Replace(temp, destination, destinationBackupFileName: null);
                return;
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }

        File.Move(temp, destination, overwrite: true);
    }

    /// <summary>
    /// R6.10, in the one place every write passes through: the Windows
    /// read-only attribute is read and named rather than met as an
    /// obscure error further down.
    /// </summary>
    internal static Result<T> RefuseIfReadOnly<T>(string full, ContainedPath path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(full)) return Result<T>.Ok(default!);

        try
        {
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly))
                return Result<T>.Fail(Outcome.Denied,
                    "the file carries the Windows read-only attribute; clear it, or pass force where the verb offers it",
                    path.Display);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return Result<T>.Ok(default!);
    }

    static void Discard(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
