// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// Line continuation: a physical line whose brackets are still open pulls
/// the following physical lines into the same logical line. Happy paths
/// run whole programs; the error paths hit the lexer directly, because
/// an unclosed bracket must be reported at the line that opened it, not
/// wherever the file happens to end.
/// </summary>
public class ContinuationTests
{
    [Fact]
    public void WrappedCallMatchesSingleLine()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Mid(\"continuation\",",
            "              5,",
            "              7))",
            ""));
        Assert.Equal("inuatio\n", output);
    }

    [Fact]
    public void ContinuationIndentIsNotSignificant()
    {
        // The wrapped tail sits at the left margin; block structure still
        // comes from the first physical line alone.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    If 1 < 2 Then",
            "        Print(Max(3,",
            "4))",
            "    Print(\"after\")",
            ""));
        Assert.Equal("4\nafter\n", output);
    }

    [Fact]
    public void ListLiteralAcrossLinesWithCommentsAndBlanks()
    {
        // Comment-only and blank physical lines inside a continuation
        // contribute no tokens; trailing comments end the physical line.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Let l = { 3, 1,      ' first pair",
            "",
            "              Rem a comment line mid-continuation",
            "              4, 1 }",
            "    Print(Sort(l))",
            ""));
        Assert.Equal("[ 1 1 3 4 ]\n", output);
    }

    [Fact]
    public void BracketsInsideStringsDoNotContinue()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(\"open ( bracket\")",
            "    Print(\"still one line\")",
            ""));
        Assert.Equal("open ( bracket\nstill one line\n", output);
    }

    [Fact]
    public void UnclosedBracketReportsOpeningLine()
    {
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    Print(Max(1, 2)",
            "    Print(3)",
            "")));
        Assert.Equal(2, e.Line);
        Assert.Contains("unclosed '('", e.Message);
    }

    [Fact]
    public void MarginDefStopsARunawayContinuation()
    {
        // A missing close paren must not swallow the next function; the
        // error points at the line that opened the bracket.
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    Print(Max(1, 2)",
            "",
            "Def Other()",
            "    Print(3)",
            "")));
        Assert.Equal(2, e.Line);
        Assert.Contains("unclosed '('", e.Message);
    }

    [Fact]
    public void LogicalLineKeepsFirstPhysicalLineNumber()
    {
        List<Line> lines = Lex(string.Join('\n',
            "Def Main()",
            "    Print(Max(1,",
            "              2))",
            "    Print(3)",
            ""));
        Assert.Equal(new[] { 1, 2, 4 }, lines.Select(l => l.LineNo));
    }

    // ---- helpers --------------------------------------------------------

    static List<Line> Lex(string src) => Lexer.ReadProgram(WriteTemp(src));

    static string RunSrc(string src)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string WriteTemp(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-continuation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(path, src);
        return path;
    }
}
