// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;
using Xunit;

namespace Shoddy.Tests;

/// <summary>
/// The file root as a BOUNDARY.
///
/// The defect these grade: a host could hand the runtime a root and the
/// runtime disciplined nothing with it. Every relative path resolved
/// against the process working directory, so `"../out.tape" WRITELINES`
/// and `"../../x.png" PLOTSAVE` wrote wherever they liked — and setting
/// the working directory to the root did not help, because that fixes
/// where a path starts and not where it may end.
///
/// Two halves, and the first matters as much as the second:
///
///   WITH NO ROOT, NOTHING CHANGED. `mill run` is a general-purpose tool
///   run by someone who could open the file in an editor anyway, and a
///   program that reads from elsewhere must go on working.
///
///   WITH A ROOT, NO FILE WORD MAY LEAVE IT. Every word, aborting and
///   guarded alike, and by the shape of its own refusal rather than a
///   uniform one.
///
/// The switch is process-global, so this joins the golden collection and
/// restores it on the way out.
/// </summary>
[Collection("golden")]
public class FileRootTests
{
    // ---- with no root, nothing changed ----

    [Fact]
    public void WithNoRootAPathMayGoWhereverItLikes()
    {
        (string root, string outside) = Sandbox();
        Assert.Equal("yes", Run(null, root, $"""
            Def Main()
                Print(Yn(TryWriteFile({Q(outside)}, "escaped")))

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """));
        Assert.True(File.Exists(outside), "with no root set, the runtime must behave as it always has");
    }

    // ---- with a root, the boundary holds ----

    [Fact]
    public void ARelativePathResolvesAgainstTheRootNotTheWorkingDirectory()
    {
        (string root, _) = Sandbox();
        // The working directory is deliberately somewhere else: a host
        // should not have to chdir for the mill's own files to land in
        // the right place.
        Assert.Equal("yes", Run(root, Path.GetTempPath(), """
            Def Main()
                Print(Yn(TryWriteFile("inside.tape", "kept")))

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(root, "inside.tape")));
    }

    [Fact]
    public void ASubdirectoryOfTheRootIsStillInsideIt()
    {
        (string root, _) = Sandbox();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Assert.Equal("yes", Run(root, root, """
            Def Main()
                Print(Yn(TryWriteFile("sub/deep.tape", "kept")))

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """));
        Assert.True(File.Exists(Path.Combine(root, "sub", "deep.tape")));
    }

    [Theory]
    [InlineData("../climbed.tape")]
    [InlineData("sub/../../climbed.tape")]
    [InlineData("./../climbed.tape")]
    public void ClimbingOutIsRefusedAndWritesNothing(string path)
    {
        (string root, string outside) = Sandbox();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Assert.Equal("no", Run(root, root, $"""
            Def Main()
                Print(Yn(TryWriteFile({Q(path)}, "escaped")))

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(outside)!, "climbed.tape")),
            "the write was refused but the bytes landed anyway");
    }

    [Fact]
    public void AnAbsolutePathSomewhereElseIsRefused()
    {
        (string root, string outside) = Sandbox();
        Assert.Equal("no", Run(root, root, $"""
            Def Main()
                Print(Yn(TryWriteFile({Q(outside)}, "escaped")))

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """));
        Assert.False(File.Exists(outside));
    }

    /// <summary>Every guarded twin refuses in ITS OWN shape — a Result
    /// for the readers, a Boolean for the writers — rather than in one
    /// uniform one. A word that answered the wrong kind of refusal would
    /// be a word whose caller could not match on it.</summary>
    [Fact]
    public void TheGuardedTwinsRefuseInTheirOwnShapes()
    {
        (string root, string outside) = Sandbox();
        File.WriteAllText(outside, "secret");
        string got = Run(root, root, $"""
            Def Main()
                Print(Says(TryReadFile("../{Path.GetFileName(outside)}")))
                Print(Yn(TryWriteFile("../w.tape", "x")))
                Print(Yn(TryDeleteFile({Q(outside)})))
                Print(Yn(FileExists({Q(outside)})))

            Def Says(r As Result) As String
                Select Case r
                    Case Ok(t)
                        "read " & t
                    Case Err(why, at)
                        "err " & why
                    Case Else
                        "none"

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """);
        string[] said = got.Trim().Replace("\r", "").Split('\n');
        Assert.StartsWith("err ", said[0]);
        Assert.Contains("OUTSIDE THE FILE ROOT", said[0]);
        Assert.Equal("no", said[1]);
        Assert.Equal("no", said[2]);
        // FILEEXISTS answers false rather than aborting: "no" is both the
        // containing answer and the true one from inside the boundary,
        // and aborting would make the word a probe for what exists
        // outside it.
        Assert.Equal("no", said[3]);
        Assert.True(File.Exists(outside), "TryDeleteFile reached outside the root");
    }

    [Theory]
    [InlineData("""ReadFile("../out.tape")""", "READFILE")]
    [InlineData("""WriteFile("../out.tape", "x")""", "WRITEFILE")]
    [InlineData("""AppendFile("../out.tape", "x")""", "APPENDFILE")]
    [InlineData("""DeleteFile("../out.tape")""", "DELETEFILE")]
    [InlineData("""BOpen("../out.tape")""", "BOPEN")]
    public void TheAbortingWordsAbortAndNameTheBoundary(string call, string word)
    {
        (string root, string outside) = Sandbox();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(outside)!, "out.tape"), "secret");
        var (exit, _, err) = Execute(root, root, $"""
            Def Main()
                Print(Str(1))
                Let ignored = {call}
                Print(Str(2))
            """);
        Assert.Equal(1, exit);
        Assert.Contains("outside the file root", err);
        Assert.Contains(word, err);
    }

    /// <summary>The word the whole boundary used to leak through: it
    /// hands its path straight to Png.Write, which no host-side path
    /// discipline could ever have reached.</summary>
    [Fact]
    public void ScribblerSaveCannotEscapeEither()
    {
        (string root, string outside) = Sandbox();
        string png = Path.Combine(Path.GetDirectoryName(outside)!, "escaped.png");
        var (exit, _, err) = ExecuteDrawing(root, root, """
            Def Main()
                Let sc = ScribblerFill(ScribblerOpen(4, 3), 10, 20, 30)
                Let s2 = ScribblerSave(sc, "../escaped.png")
                Print(Str(ScribblerWidth(s2)))
            """);
        Assert.Equal(1, exit);
        Assert.Contains("outside the file root", err);
        Assert.False(File.Exists(png), "PLOTSAVE's own path leaked past the root");
    }

    /// <summary>A link planted inside the root and pointing out of it is
    /// followed before the comparison, not after. Skipped where the
    /// platform will not let a test create one — an unprivileged Windows
    /// account cannot without developer mode.</summary>
    [Fact]
    public void ALinkOutOfTheRootIsFollowedBeforeTheCheck()
    {
        (string root, string outside) = Sandbox();
        string away = Path.Combine(Path.GetDirectoryName(outside)!, "away");
        Directory.CreateDirectory(away);
        try { Directory.CreateSymbolicLink(Path.Combine(root, "door"), away); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        Assert.Equal("no", Run(root, root, """
            Def Main()
                Print(Yn(TryWriteFile("door/through.tape", "escaped")))

            Def Yn(b As Boolean) As String
                If b Then
                    "yes"
                Else
                    "no"
            """));
        Assert.False(File.Exists(Path.Combine(away, "through.tape")));
    }

    // ---- the harness ----

    /// <summary>A root, and a path just outside it. The root is nested
    /// one level down so that `..` has somewhere to land that the test
    /// can look at.</summary>
    static (string Root, string Outside) Sandbox()
    {
        string box = Path.Combine(Path.GetTempPath(), "shoddy-fileroot", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(box, "root");
        Directory.CreateDirectory(root);
        return (root, Path.Combine(box, "outside.tape"));
    }

    static string Q(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";

    /// <summary>What the program printed, with Print's closing newline
    /// off so a single-line answer compares as the word it is.</summary>
    static string Run(string? root, string cwd, string src)
    {
        var (exit, output, err) = Execute(root, cwd, src);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {err}");
        return output.Replace("\r", "").TrimEnd('\n');
    }

    static (int Exit, string Out, string Err) Execute(string? root, string cwd, string src) =>
        Execute(root, cwd, src, drawing: false);

    static (int Exit, string Out, string Err) ExecuteDrawing(string? root, string cwd, string src) =>
        Execute(root, cwd, src, drawing: true);

    /// <summary>Weave and run, with the ambient switch set the way a host
    /// sets it and the working directory pointed somewhere of the
    /// caller's choosing — the two things that used to be conflated.</summary>
    static (int Exit, string Out, string Err) Execute(string? root, string cwd, string src, bool drawing)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-fileroot-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(file, src);

        string? wasRoot = Environment.GetEnvironmentVariable(FileRoot.Variable);
        string wasCwd = Directory.GetCurrentDirectory();
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        if (drawing)
            ScribblerRegistry.CreateScribbler = (w, h) =>
                new ScribblerHandle { Width = w, Height = h, Pixels = new byte[w * h * 4] };
        try
        {
            Environment.SetEnvironmentVariable(FileRoot.Variable, root);
            Directory.SetCurrentDirectory(cwd);
            var machines = new MachineSet();
            List<Line> lines = Lexer.ReadProgram(file, machines.TryResolve);
            var prog = new ShoddyProgram();
            machines.SeedInto(prog);
            Parser.Parse(lines, prog);
            var output = new StringWriter();
            int exit = Weaver.Execute(prog, machines.Machines, output,
                                      TextReader.Null, Array.Empty<string>());
            return (exit, output.ToString(), err.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileRoot.Variable, wasRoot);
            Directory.SetCurrentDirectory(wasCwd);
            Console.SetError(oldErr);
            if (drawing) ScribblerRegistry.CreateScribbler = null;
        }
    }
}
