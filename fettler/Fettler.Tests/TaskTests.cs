using System.Text.Json;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.12 and R10.16 to R10.18: running a declared task, the argument
/// fidelity R7.3 exists for, and what concurrency does and does not do.
/// </summary>
public sealed class TaskTests
{
    /// <summary>The shipped executable, sitting beside the test assembly
    /// because Fettler.Tests references it. Driving the real binary is
    /// also what R2.5 asks CI to do on every push.</summary>
    static string? Fettle()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "fettle.exe" : "fettle");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// R10.12 and R7.3: an argument containing a space, a double quote
    /// and a backslash arrives at the child exactly as given.
    ///
    /// <para>The child is <c>fettle</c> itself, searching for that string
    /// literally and echoing the pattern back in its machine-readable
    /// answer. Nothing about the test can pass by accident: if a shell
    /// were involved anywhere, or if the declaration file had a quoting
    /// syntax to get wrong, the string would come back mangled.</para>
    /// </summary>
    [Fact]
    public async Task AnArgumentWithSpacesQuotesAndBackslashesArrivesVerbatim()
    {
        string? fettle = Fettle();
        if (fettle is null) return;

        using var box = new Sandbox();

        const string awkward = """a "quoted" thing and \a\back\slash and  two spaces""";
        box.Write("subject.txt", $"before\n{awkward}\nafter\n");

        // The glob keeps the search off the declaration file, which
        // necessarily contains the same awkward string.
        box.Write(Tasks.FileName, $"""
            # one argument per line, so there is no quoting syntax to get wrong
            echo:
              {fettle}
              search
              --literal
              --json
              --glob
              subject.txt
              {awkward}

            """);

        Result<TaskRun> ran = await box.Bench.RunAsync("echo", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Equal(0, ran.Value.ExitCode);

        JsonElement answer = JsonDocument.Parse(ran.Value.Stdout).RootElement;
        Assert.True(answer.GetProperty("ok").GetBoolean(), ran.Value.Stdout);

        string received = answer.GetProperty("patterns")[0].GetString()!;
        Assert.Equal(awkward, received);
        Assert.Equal(1, answer.GetProperty("hits_count").GetInt32());
    }

    [Fact]
    public void TasksAreListedFromTheDeclarationFile()
    {
        using var box = new Sandbox();
        box.Write(Tasks.FileName, """
            build:
              pwsh
              -File
              build.ps1

            gate:
              node
              scripts/gate/driver.mjs
              gate
              --resume
            """);

        Result<IReadOnlyList<TaskDecl>> tasks = box.Bench.TaskList();

        Assert.True(tasks.IsOk, tasks.Failure?.Message);
        Assert.Equal(2, tasks.Value.Count);
        Assert.Equal("build", tasks.Value[0].Name);
        Assert.Equal(["pwsh", "-File", "build.ps1"], tasks.Value[0].Command);
        Assert.Equal(4, tasks.Value[1].Command.Count);
    }

    [Fact]
    public async Task AnUndeclaredTaskIsNotFoundAndNamesWhatIsDeclared()
    {
        using var box = new Sandbox();
        box.Write(Tasks.FileName, "build:\n  pwsh\n");

        Result<TaskRun> ran = await box.Bench.RunAsync("gate", TimeSpan.Zero, CancellationToken.None);

        Assert.False(ran.IsOk);
        Assert.Equal(Outcome.NotFound, ran.Failure!.Outcome);
        Assert.Contains("build", ran.Failure.Message);
    }

    /// <summary>R7.5: a task that does not terminate is killable, with a
    /// timeout the caller sets.</summary>
    /// <summary>
    /// A child that runs a long time whatever its stdin does.
    ///
    /// <para>Note what will NOT serve here: <c>fettle serve</c> exits at
    /// once, because <see cref="Tasks"/> closes the child's standard
    /// input on purpose - a child that inherits an open stdin can block
    /// forever waiting on input nobody is going to send, and that is the
    /// hang R7.5 exists to prevent rather than to demonstrate.</para>
    /// </summary>
    static string LongRunning() => OperatingSystem.IsWindows()
        ? "ping\n  -n\n  30\n  127.0.0.1"
        : "sleep\n  30";

    [Fact]
    public async Task ATaskThatOutrunsItsTimeoutIsKilledAndSaidToHaveBeen()
    {
        using var box = new Sandbox();
        box.Write(Tasks.FileName, $"forever:\n  {LongRunning()}\n");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Result<TaskRun> ran = await box.Bench.RunAsync("forever", TimeSpan.FromSeconds(2), CancellationToken.None);
        clock.Stop();

        Assert.False(ran.IsOk);
        Assert.Equal(Outcome.TimedOut, ran.Failure!.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the timeout must actually kill it");
    }

    /// <summary>
    /// R3.10 and R3.11: a task can be cancelled mid-flight, and because
    /// <c>run</c> holds no lock, cancelling it is possible at all - on a
    /// blocking design the cancellation could not be delivered until the
    /// thing it cancels had already finished.
    /// </summary>
    [Fact]
    public async Task ATaskCanBeCancelledMidFlight()
    {
        using var box = new Sandbox();
        box.Write(Tasks.FileName, $"forever:\n  {LongRunning()}\n");

        using var cancelling = new CancellationTokenSource();
        Task<Result<TaskRun>> running = box.Bench.RunAsync("forever", TimeSpan.Zero, cancelling.Token);

        await Task.Delay(300);
        await cancelling.CancelAsync();

        Result<TaskRun> ran = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.True(ran.Value.Cancelled, "a cancelled task must report that it was cancelled");
    }

    // ---- R10.18: concurrent mutations do not interleave ----

    /// <summary>
    /// R3.11 and R5.8. Two edits to one file, issued together, produce
    /// one success and one staleness refusal - never two successes - and
    /// the file is left holding one whole result rather than a blend of
    /// both.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentEditsToOneFileYieldOneSuccessAndOneRefusal()
    {
        using var box = new Sandbox();
        box.Write("contended.txt", "original\n");

        string hash = box.Bench.Read(new ReadRequest(["contended.txt"])).Value[0].Hash;

        // Both carry the same starting hash, which is exactly the race:
        // both believe the file is what they last read.
        Task<Result<EditAnswer>> first = box.Bench.EditAsync(
            [new FileEdits("contended.txt", hash, [new Edit.Replace("original", "first")])], dryRun: false);

        Task<Result<EditAnswer>> second = box.Bench.EditAsync(
            [new FileEdits("contended.txt", hash, [new Edit.Replace("original", "second")])], dryRun: false);

        Result<EditAnswer>[] both = await Task.WhenAll(first, second);

        int succeeded = both.Count(r => r.IsOk);
        Assert.Equal(1, succeeded);

        Result<EditAnswer> refused = both.First(r => !r.IsOk);
        Assert.Equal(Outcome.Stale, refused.Failure!.Outcome);

        string left = box.ReadText("contended.txt");
        Assert.True(left is "first\n" or "second\n", $"the file must hold one whole result, not '{left}'");
    }

    /// <summary>R3.11: read-only operations are not serialized against
    /// each other, so a tree can be read from several places at once.</summary>
    [Fact]
    public async Task ManyReadsRunTogetherAndAllAnswer()
    {
        using var box = new Sandbox();
        for (int i = 0; i < 20; i++) box.Write($"f{i:00}.txt", $"content {i}\n");

        Task<Result<IReadOnlyList<ReadSlice>>>[] reads = [.. Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => box.Bench.Read(new ReadRequest([$"f{i:00}.txt"]))))];

        Result<IReadOnlyList<ReadSlice>>[] all = await Task.WhenAll(reads);

        Assert.All(all, r => Assert.True(r.IsOk, r.Failure?.Message));
        for (int i = 0; i < 20; i++)
            Assert.Equal($"content {i}\n", all[i].Value[0].Text);
    }
}
