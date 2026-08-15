using System.Diagnostics;

namespace Fettler.Core;

/// <summary>One declared task: a name and the argument list it runs.</summary>
public sealed record TaskDecl(string Name, IReadOnlyList<string> Command, string? WorkingDirectory)
{
    /// <summary>How the task reads in a listing. Purely for display -
    /// nothing ever parses this back, which is the point.</summary>
    public string Display => string.Join(' ', Command);
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
/// reaches back into the declaration file: <b>the file holds one
/// argument per line</b>, so there is no quoting syntax in it to get
/// wrong. A file that cannot express a quoting rule cannot carry a
/// quoting bug.</para>
///
/// <para><b>The file is trusted input, and R7.6 says so out loud.</b>
/// R8's containment guards paths; it does not and cannot guard the
/// command list, which is read from a file inside the very root Fettler
/// was pointed at. Whoever can write that file chooses what a caller -
/// a model included - is able to execute. That is a deliberate position:
/// the alternative is an allowlist maintained outside the repository,
/// which puts the declaration somewhere the repository's own
/// contributors cannot change alongside the build it belongs to.</para>
/// </summary>
public static class Tasks
{
    /// <summary>The declaration file, at the root, by this fixed name so
    /// nothing has to search for it or be told where it is (R7.6).</summary>
    public const string FileName = "fettle-tasks";

    /// <summary>
    /// Read the declared tasks.
    ///
    /// <para>The format, in full: a line ending in a colon opens a task;
    /// every indented line after it is one argument, verbatim, with no
    /// quoting and no escaping; a line starting with <c>#</c> is a
    /// comment; blank lines are ignored. An indented line beginning
    /// <c>cwd:</c> sets the working directory, which is checked for
    /// containment like any other path (R8.2).</para>
    /// </summary>
    public static Result<IReadOnlyList<TaskDecl>> Read(Roots roots)
    {
        Result<ContainedPath> path = roots.Resolve(FileName);
        if (!path.IsOk) return path.Carry<IReadOnlyList<TaskDecl>>();

        if (!File.Exists(path.Value.Full))
            return Result<IReadOnlyList<TaskDecl>>.Ok([]);

        Result<TextFile> read = TextIo.Read(path.Value);
        if (!read.IsOk) return read.Carry<IReadOnlyList<TaskDecl>>();

        var tasks = new List<TaskDecl>();
        string? name = null;
        string? cwd = null;
        var command = new List<string>();

        void Close()
        {
            if (name is not null && command.Count > 0)
                tasks.Add(new TaskDecl(name, command.ToArray(), cwd));
            name = null;
            cwd = null;
            command.Clear();
        }

        int number = 0;
        foreach (Line line in read.Value.Lines)
        {
            number++;
            string text = line.Text;
            if (text.TrimStart().StartsWith('#') || text.Trim().Length == 0) continue;

            bool indented = text.Length > 0 && (text[0] == ' ' || text[0] == '\t');

            // An argument keeps everything after its indentation,
            // including trailing spaces: R7.3 promises the child receives
            // what was written, and a format that quietly trims cannot
            // express an argument that ends in a space.
            string body = indented ? text.TrimStart(' ', '\t') : text.Trim();

            if (!indented)
            {
                Close();
                if (!body.EndsWith(':'))
                    return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                        $"{FileName} line {number}: a task name ends with a colon", path.Value.Display);

                name = body[..^1].Trim();
                if (name.Length == 0)
                    return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                        $"{FileName} line {number}: the task has no name", path.Value.Display);
                continue;
            }

            if (name is null)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"{FileName} line {number}: an argument before any task name", path.Value.Display);

            if (body.StartsWith("cwd:", StringComparison.OrdinalIgnoreCase))
            {
                cwd = body[4..].Trim();
                continue;
            }

            command.Add(body);
        }

        Close();
        return Result<IReadOnlyList<TaskDecl>>.Ok(tasks);
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
                ? $"there is no {FileName} at the root, so no task is declared"
                : $"no task called '{name}'; {FileName} declares: {string.Join(", ", all.Value.Select(t => t.Name))}");
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

        string workingDirectory;
        if (task.WorkingDirectory is { } declared)
        {
            // R8.2 explicitly includes the working directory of a task.
            Result<ContainedPath> cwd = roots.Resolve(declared);
            if (!cwd.IsOk) return cwd.Carry<TaskRun>();
            workingDirectory = cwd.Value.Full;
        }
        else
        {
            workingDirectory = roots.PathOf(roots.Names[0]);
        }

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
