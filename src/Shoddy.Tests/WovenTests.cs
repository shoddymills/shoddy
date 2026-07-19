using Shoddy.Compiler;
using Shoddy.Devil;

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
    static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact] public void Examples() => RunGolden("examples.shoddy", "examples.out");
    [Fact] public void LibTest() => RunGolden("libtest.shoddy", "libtest.out");
    [Fact] public void Simplex() => RunGolden("simplex.shoddy", "simplex.out");
    [Fact] public void Gradebook() => RunGolden("gradebook.shoddy", "gradebook.out", stdinFile: "gradebook.in");

    // MPS machine -> simplex machine -> sensitivity report; Input reads
    // EOF, selecting the dat/mix.mps default.
    [Fact] public void SimplexMps() => RunGolden("simplex-mps.shoddy", "simplex-mps.out");

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

    static string RunWoven(string sbPath, TextReader input)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(sbPath));
        var output = new StringWriter();
        Assert.Equal(0, Weaver.Execute(prog, null, output, input));
        return output.ToString();
    }
}
