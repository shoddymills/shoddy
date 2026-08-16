using System.Diagnostics;

namespace Fettler.Core;

/// <summary>One declared task: a name, the argument list it runs, and the
/// line it was declared as.</summary>
public sealed record TaskDecl(
    string Name, IReadOnlyList<string> Command, string? WorkingDirectory, string? Line = null)
{
    /// <summary>How the task reads in a listing: the line as declared,
    /// quotes and all. Rejoining the split words would lose the quoting
    /// that made an argument one argument, and a listing that cannot be
    /// compared with the file is a listing nobody can check.</summary>
    public string Display => Line ?? string.Join(' ', Command);
}

/// <summary>What running a task produced.</summary>
public sealed record TaskRun(
    string Name,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut,
    bool Cancelled);

/// <summary>
/// The declared-task facility of R7, so builds, tests and gates can be
/// invoked without composing a shell command.
///
/// <para><b>No shell is involved, ever (R7.3).</b> The program is
/// launched with its arguments as a list, not as a string for an
/// interpreter to re-split. That single clause removes the entire
/// quoting-and-dialect failure class this tool exists to escape, and it
/// reaches back into the declaration: the command line is split ONCE, by
/// <see cref="Split"/>, under a grammar small enough to state in a
/// sentence - and what the child receives is the list that produced,
/// never a string for an interpreter to have an opinion about.</para>
///
/// <para><b>Tasks are declared in <c>.fettler.json</c></b>, beside the
/// trees, because what may run in a tree and what may be done to it are
/// one statement about one tree.</para>
///
/// <para><b>That configuration is trusted input, and R7.6 says so out
/// loud.</b> R8's containment guards paths; it does not and cannot guard
/// a command list, which is read from a file inside the very root Fettler
/// was pointed at. Whoever can write it chooses what a caller - a model
/// included - is able to execute. That is a deliberate position: the
/// alternative is an allowlist maintained outside the repository, which
/// puts the declaration somewhere the repository's own contributors
/// cannot change alongside the build it belongs to.</para>
/// </summary>
public static class Tasks
{
    /// <summary>The declared tasks, as the configuration gave them.</summary>
    public static Result<IReadOnlyList<TaskDecl>> Read(Roots roots) =>
        Result<IReadOnlyList<TaskDecl>>.Ok(roots.Tasks);

    /// <summary>
    /// The words of a declared command line, split once, here.
    ///
    /// <para><b>The whole grammar:</b> whitespace separates arguments; a
    /// double-quoted span is one argument or part of one, and the quotes
    /// are removed; two double quotes inside a quoted span are one literal
    /// quote. Nothing else is special - no single quotes, no variables, no
    /// globbing, no redirection, no operators.</para>
    ///
    /// <para><b>A backslash is never an escape.</b> It is an ordinary
    /// character, so <c>"C:\Program Files\pwsh.exe"</c> means what it looks
    /// like. Making it an escape would require every Windows path in the
    /// file to be doubled, and a path that has to be doubled is a path that
    /// will one day not be.</para>
    ///
    /// <para><b>The split happens once and the result is a list.</b> The
    /// program is launched with that list, never with a command line for
    /// something else to re-split, so the child receives exactly the words
    /// this method produced.</para>
    /// </summary>
    public static Result<IReadOnlyList<string>> Split(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        bool quoted = false, started = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { word.Append('"'); i++; }
                else quoted = !quoted;
                started = true;
                continue;
            }

            if (!quoted && (c == ' ' || c == '\t'))
            {
                if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
                continue;
            }

            word.Append(c);
            started = true;
        }

        if (quoted)
            return Result<IReadOnlyList<string>>.Fail(Outcome.Invalid,
                "the command has a double quote that is never closed");

        if (started) words.Add(word.ToString());
        return Result<IReadOnlyList<string>>.Ok(words);
    }

    public static Result<TaskDecl> Find(Roots roots, string name)
    {
        Result<IReadOnlyList<TaskDecl>> all = Read(roots);
        if (!all.IsOk) return all.Carry<TaskDecl>();

        foreach (TaskDecl task in all.Value)
            if (task.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Result<TaskDecl>.Ok(task);

        return Result<TaskDecl>.Fail(Outcome.NotFound,
            all.Value.Count == 0
                ? $"no task is declared; add a \"tasks\" object to {RootsFile.FileName}"
                : $"no task called '{name}'; {RootsFile.FileName} declares: {string.Join(", ", all.Value.Select(t => t.Name))}");
    }

    /// <summary>
    /// Run a declared task, capturing both streams and the exit code
    /// (R7.4) and killable by timeout or by cancellation (R7.5, R3.10).
    ///
    /// <para>Nothing here ever writes to the process's own stdout, which
    /// is what keeps R3.2's "stdout carries the protocol and nothing
    /// else" true while a child is running.</para>
    /// </summary>
    public static async Task<Result<TaskRun>> RunAsync(
        Roots roots, string name, TimeSpan timeout, CancellationToken cancel)
    {
        Result<TaskDecl> found = Find(roots, name);
        if (!found.IsOk) return found.Carry<TaskRun>();
        TaskDecl task = found.Value;

        // B.8: the working directory decides. `execute` is never granted
        // by default in any tree, including the one the configuration
        // sits in, because that tree is writable by definition and the
        // configuration lives inside it - write-by-default plus
        // execute-by-default is arbitrary code execution, and it defeats
        // every other permission at once, since a scope protected from
        // `delete` means nothing to a task that can run `del`.
        //
        // R8.2 explicitly includes the working directory of a task.
        Result<ContainedPath> cwd = roots.Resolve(
            task.WorkingDirectory ?? $"{roots.Names[0]}:", Permission.Execute);

        if (!cwd.IsOk)
            return cwd.Failure!.Outcome == Outcome.Refused
                ? Result<TaskRun>.Fail(Outcome.Refused,
                    $"'{task.Name}' would run in a tree that does not grant execute. "
                    + "Add \"execute\" to that tree's or scope's \"can\" in "
                    + $"{RootsFile.FileName}. It is never granted by default.",
                    cwd.Failure.Path)
                : cwd.Carry<TaskRun>();

        string workingDirectory = cwd.Value.Full;

        var info = new ProcessStartInfo
        {
            FileName = task.Command[0],
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // The clause that removes the quoting failure class: an
            // argument list, never a command string for something else
            // to re-split.
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        for (int i = 1; i < task.Command.Count; i++) info.ArgumentList.Add(task.Command[i]);

        using var process = new Process { StartInfo = info };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result<TaskRun>.Fail(Outcome.NotFound,
                $"could not start '{task.Command[0]}': {e.Message}");
        }

        // A child that inherits an open stdin can block forever waiting
        // on input nobody is going to send.
        process.StandardInput.Close();

        Task<string> readOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> readErr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeout > TimeSpan.Zero) deadline.CancelAfter(timeout);

        bool timedOut = false, cancelled = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancel.IsCancellationRequested;
            cancelled = cancel.IsCancellationRequested;
            Kill(process);
        }

        stdout.Write(await Settled(readOut).ConfigureAwait(false));
        stderr.Write(await Settled(readErr).ConfigureAwait(false));
        stopwatch.Stop();

        int exit = timedOut || cancelled ? -1 : SafeExitCode(process);

        var run = new TaskRun(task.Name, exit, stdout.ToString(), stderr.ToString(),
            stopwatch.Elapsed, timedOut, cancelled);

        if (timedOut)
            return Result<TaskRun>.Fail(new Failure(Outcome.TimedOut,
                $"'{task.Name}' outran its timeout of {timeout.TotalSeconds:0}s and was killed"));

        return Result<TaskRun>.Ok(run);
    }

    /// <summary>A task killed mid-flight leaves its whole process tree
    /// behind unless the tree is what gets killed.</summary>
    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception) { }
    }

    static async Task<string> Settled(Task<string> reading)
    {
        try { return await reading.ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { return string.Empty; }
    }

    static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
