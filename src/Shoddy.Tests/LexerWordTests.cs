// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// What may and may not appear inside a word. Words split on whitespace
/// and the self-delimiting set alone, so an operator written hard against
/// a name — <c>count+1</c> — is one word, and used to survive lexing and
/// die much later as an undefined word. The lexer now says what is
/// actually wrong.
///
/// The interesting half of this is what the check must NOT catch: the
/// operators themselves, number literals with a signed exponent, the
/// backslash inside a string, and above all <c>y'</c>, which is exactly
/// the name a beginner reaches for and the reason <c>'</c> and <c>-</c>
/// are outside the character set.
/// </summary>
public class LexerWordTests
{
    // ---- rejected: an operator jammed into a name ------------------------

    [Theory]
    [InlineData("count*1", '*')]
    [InlineData("count+1", '+')]
    [InlineData("count/1", '/')]
    [InlineData("x<y", '<')]
    [InlineData("x>y", '>')]
    [InlineData("x=y", '=')]
    [InlineData("total^2", '^')]
    [InlineData("total&suffix", '&')]
    public void OperatorInsideAWordIsRejected(string word, char op)
    {
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    Print(" + word + ")",
            "")));
        Assert.Equal(2, e.Line);
        Assert.Contains($"'{op}' is an operator", e.Message);
        Assert.Contains("count + 1", e.Message);
    }

    [Fact]
    public void ApostropheIsNotWhatBreaksAPrimedComparison()
    {
        // x>y' must fail for the > and nothing else — the ' is a name
        // character and stays one.
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    Print(x>y')",
            "")));
        Assert.Contains("'>' is an operator", e.Message);
    }

    [Fact]
    public void OperatorIsNamedEvenWhenTheWordLeadsWithUnaryMinus()
    {
        // The check runs before -N is split, so -count+1 is diagnosed as
        // the missing space it is rather than becoming - then count+1.
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    Print(-count+1)",
            "")));
        Assert.Contains("'+' is an operator", e.Message);
    }

    // ---- rejected: ; and \, which spell nothing at all -------------------

    [Theory]
    [InlineData(";")]
    [InlineData("Print(\"HI\");")]
    [InlineData("a;b")]
    [InlineData("C:\\notes.txt")]
    [InlineData("back\\slash")]
    [InlineData("\\")]
    public void SemicolonAndBackslashAreReservedOutright(string line)
    {
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    " + line,
            "")));
        Assert.Equal(2, e.Line);
        Assert.Contains("the characters ; \\ are reserved", e.Message);
    }

    // ---- untouched: the operators themselves -----------------------------

    [Fact]
    public void StandaloneOperatorsStillWork()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(7 + 2)",
            "    Print(7 - 2)",
            "    Print(7 * 2)",
            "    Print(7 / 2)",
            "    Print(7 = 2)",
            "    Print(7 <> 2)",
            "    Print(7 < 2)",
            "    Print(7 > 2)",
            "    Print(7 <= 2)",
            "    Print(7 >= 2)",
            "    Print(7 ^ 2)",
            "    Print(\"ab\" & \"cd\")",
            ""));
        Assert.Equal("9\n5\n14\n3.5\nFalse\nTrue\nFalse\nTrue\nFalse\nTrue\n49\nabcd\n",
                     output);
    }

    [Fact]
    public void OperatorSectionsAndBareOperatorsStillWork()
    {
        // >=(100) is a section and + is a bare function value: both are
        // whole-word operators reaching the lexer with a bracket or a
        // comma against them, not a name.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Fold({ 1, 2, 3 }, 0, +))",
            "    Print(Length(Filter({ 40, 100, 140 }, >=(100))))",
            ""));
        Assert.Equal("6\n2\n", output);
    }

    [Fact]
    public void StackEffectDoubleHyphenStillWorks()
    {
        // -- is the effect signature's separator and leads with the one
        // character deliberately left out of the operator set.
        List<Line> lines = Lex(string.Join('\n',
            "Def Twice [ n -- n n ]",
            "    Dup",
            "",
            "Def Main()",
            "    Print(Len({ 1 }))",
            ""));
        Assert.Contains(lines[0].Toks, t => !t.IsStr && t.Text == "--");
    }

    // ---- untouched: numbers, strings, and y' ------------------------------

    [Fact]
    public void SignedExponentLiteralsAreStillNumbers()
    {
        // 1E+10 carries a + next to digits and is the one legitimate word
        // that mixes the two, so the check exempts anything that lexes as
        // a number. 1E-10 never depended on the exemption (- is outside
        // the set), and the two must not diverge.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(1e+10)",
            "    Print(1e-10 * 1e10)",
            "    Print(-1.5E+3)",
            ""));
        Assert.Equal("10000000000\n1\n-1500\n", output);
    }

    [Fact]
    public void BackslashKeepsItsMeaningInsideAString()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(\"a\\\\b\")",
            "    Print(\"say \\\"hi\\\"\")",
            "    Print(Len(\"x\\ty\"))",
            ""));
        Assert.Equal("a\\b\nsay \"hi\"\n3\n", output);
    }

    [Fact]
    public void PrimedNamesRemainValidAndDistinct()
    {
        // The whole reason ' is outside the character set: y and y' are
        // two names, and a beginner writing y' for "the next y" must keep
        // getting a name rather than an error.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Let y = 1",
            "    Let y' = 2",
            "    Print(y)",
            "    Print(y')",
            "    Print(y + y')",
            ""));
        Assert.Equal("1\n2\n3\n", output);
    }

    [Fact]
    public void SigilMessageIsUnchanged()
    {
        // The sigils keep their own message: they are reserved on
        // principle, where an operator in a word is a missing space.
        var e = Assert.Throws<ShoddyError>(() => Lex(string.Join('\n',
            "Def Main()",
            "    Let name$ = \"ADA\"",
            "")));
        Assert.Contains("the characters ! % # $ ? are reserved", e.Message);
    }

    // ---- helpers ---------------------------------------------------------

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
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-lexer-word", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(path, src);
        return path;
    }
}
