// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The constitution: the mill is correct when the five programs in tst/
/// produce byte-identical stdout against tst/golden/. Each program is
/// woven to memory and executed in-process — exactly the `mill run`
/// path. libtest's file I/O writes relative paths, so the working
/// directory is pinned to the repo root (the "golden" collection runs
/// serially, making the process-global cwd change safe).
/// </summary>
[Collection("golden")]
public class WovenTests
{
    static readonly string Root = RepoRoot.Dir;

    [Fact] public void Examples() => RunGolden("examples.shoddy", "examples.out");
    [Fact] public void LibTest() => RunGolden("libtest.shoddy", "libtest.out");
    [Fact] public void Simplex() => RunGolden("simplex.shoddy", "simplex.out");
    [Fact] public void Gradebook() => RunGolden("gradebook.shoddy", "gradebook.out", stdinFile: "gradebook.in");

    [Fact]
    public void TailRecursionBecomesALoop()
    {
        // A million-deep self-tail-recursion must run as a loop.
        string src = string.Join('\n',
            "Def Count(n As Number) As Number",
            "    If n = 0 Then",
            "        \"DONE\"",
            "    Else",
            "        Count(n - 1)",
            "",
            "Def Main()",
            "    Print(Count(1000000))",
            "");
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-woven", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sb = Path.Combine(dir, "tail.shoddy");
        File.WriteAllText(sb, src);
        Assert.Equal("DONE\n", RunWoven(sb, TextReader.Null));
    }

    [Fact]
    public void MutualTailRecursionBecomesAJump()
    {
        // A million-deep recursion between two Defs, every call in tail
        // position, must run flat. This runs on the test thread's own
        // 1 MB stack on purpose: a frame per call would kill the process
        // at about twenty thousand, so passing here proves the jump.
        string src = string.Join('\n',
            "Def IsEven(n As Number) As Boolean",
            "    If n = 0 Then",
            "        True",
            "    Else",
            "        IsOdd(n - 1)",
            "",
            "Def IsOdd(n As Number) As Boolean",
            "    If n = 0 Then",
            "        False",
            "    Else",
            "        IsEven(n - 1)",
            "",
            "Def Main()",
            "    Print(IsEven(1000000))",
            "    Print(IsOdd(1000000))",
            "");
        Assert.Equal("True\nFalse\n", RunSource("mutual", src));
    }

    [Fact]
    public void ListPatternsTakeAListApart()
    {
        // Case Empty and Case Prepend(head, tail), guarded, nested in the
        // head and in the tail, and falling through to Case Else.
        string src = string.Join('\n',
            "Type Pr",
            "    Fst As Number",
            "    Snd As String",
            "",
            "Def Describe(xs As List Of Number) As String",
            "    Select Case xs",
            "        Case Empty",
            "            \"EMPTY\"",
            "        Case Prepend(x, Empty)",
            "            Str(x) & \" ALONE\"",
            "        Case Prepend(x, more) Where x > 100",
            "            \"BIG \" & Str(x) & \" THEN \" & Str(Length(more))",
            "        Case Prepend(x, more)",
            "            Str(x) & \" THEN \" & Str(Length(more))",
            "",
            "Def FirstFst(ps As List Of Pr) As Number",
            "    Select Case ps",
            "        Case Prepend(Pr(a, b), more)",
            "            a",
            "        Case Else",
            "            0",
            "",
            "Def Main()",
            "    Print(Describe({ }))",
            "    Print(Describe({ 7 }))",
            "    Print(Describe({ 7, 8, 9 }))",
            "    Print(Describe({ 500, 8 }))",
            "    Print(FirstFst({ Pr(4, \"A\"), Pr(5, \"B\") }))",
            "    Print(FirstFst({ }))",
            "");
        Assert.Equal("EMPTY\n7 ALONE\n7 THEN 2\nBIG 500 THEN 1\n4\n0\n", RunSource("patterns", src));
    }

    /// <summary>Weave and run a program given as source, from a fresh
    /// directory beside the repo's machines so an Include resolves.</summary>
    static string RunSource(string name, string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-woven", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sb = Path.Combine(dir, name + ".shoddy");
        File.WriteAllText(sb, src);
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);
        var output = new StringWriter();
        Assert.Equal(0, Weaver.Execute(parsed, machines.Machines, output, TextReader.Null, null));
        return output.ToString();
    }

    static void RunGolden(string program, string golden, string? stdinFile = null)
    {
        using TextReader input = stdinFile != null
            ? new StreamReader(Path.Combine(Root, "tst", "golden", stdinFile))
            : TextReader.Null;
        string cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Root;
        string actual;
        try
        {
            actual = RunWoven(Path.Combine(Root, "tst", program), input);
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
        string expected = File.ReadAllText(Path.Combine(Root, "tst", "golden", golden));
        Assert.Equal(expected, actual);
    }

    static string RunWoven(string sbPath, TextReader input, string[]? args = null)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(sbPath));
        var output = new StringWriter();
        Assert.Equal(0, Weaver.Execute(prog, null, output, input, args));
        return output.ToString();
    }
}
