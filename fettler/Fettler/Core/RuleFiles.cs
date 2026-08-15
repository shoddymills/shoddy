namespace Fettler.Core;

/// <summary>
/// The files that tell Fettler what it may do, which Fettler may
/// therefore never write.
///
/// <para><b>This is the rule that makes every other rule hold.</b>
/// Without it the permission model is decoration, and the demonstration
/// is two commands long: declare a tree, write <c>fettle-tasks</c> into
/// it, run what you just wrote. That was live in this tool, and it
/// defeats every other permission at once - a scope protected from
/// <c>delete</c> means nothing to a task that can run <c>del</c>.</para>
///
/// <para><b>An explicit grant is not enough on its own either.</b> If the
/// task file can be written by the thing the grant applies to, the grant
/// has no bound: whoever holds <see cref="Permission.Execute"/> also
/// chooses what executes. So the refusal is compiled in, applies at every
/// permission level, and is not configurable.</para>
///
/// <para><b>It is its own outcome and its own wording</b>, not a
/// permission denial. Nothing is missing. A caller told "you lack update"
/// looks for the grant that would fix it; there is no such grant, and
/// saying so is the honest answer.</para>
///
/// <para><b>The bound, stated:</b> this is reliable, not tamper-proof. It
/// stops an assistant reaching these files through a tool call. It does
/// not stop the person who wrote them opening them in an editor, and
/// nothing here should try - that person is the author of the rules, not
/// a threat to them.</para>
/// </summary>
public static class RuleFiles
{
    /// <summary>The three names, closed and compiled in.</summary>
    public static readonly string[] Names =
    [
        RootsFile.FileName,
        RootsFile.LocalFileName,
        Tasks.FileName,
    ];

    /// <summary>
    /// Whether a path names one of them.
    ///
    /// <para>Matched on the file name alone, at any depth: a
    /// <c>.fettler.json</c> in a subdirectory governs that subtree when a
    /// command is run from inside it, so protecting only the one at the
    /// top would leave every other one writable. Matched by name and not
    /// by prefix, so <c>fettle-tasks.md</c> and <c>.fettler.json.bak</c>
    /// are ordinary files - they govern nothing, and refusing them would
    /// be superstition rather than a rule.</para>
    /// </summary>
    public static bool Governs(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        foreach (string ruled in Names)
            if (name.Equals(ruled, Roots.PathComparison)) return true;
        return false;
    }

    /// <summary>The refusal, worded once so every path that can put bytes
    /// somewhere says the same thing.</summary>
    public static Result<T> Refuse<T>(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        return Result<T>.Fail(Outcome.Governed,
            $"{name} is one of the files that tell this tool what it may do, "
            + "and it does not edit those. Change it with an editor. "
            + $"The files are: {string.Join(", ", Names)}",
            path);
    }
}
