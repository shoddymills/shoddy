// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// CODES, FROMCODES, CODEAT and INSTRFROM — the four words that let a
/// scanner read a character without cutting a one-character string out
/// first.
///
/// THE REFUSALS ARE WHY THIS FILE IS IN C#. Shoddy has no catchable
/// errors, so a tst/*.shoddy suite dies on the first abort and can assert
/// at most one of them. Every refusal below is a separate program run to
/// its own death, which is the only way to walk all nine.
///
/// The point of CODEAT is the refusal itself. Asc(Mid(s, k, 1)) is the
/// natural spelling and it is a trap: MID clamps a start past the end to
/// the empty string, and ASC then aborts naming ASC, three Defs away from
/// whatever computed k, and only on inputs that reach the end. CODEAT
/// aborts at the mistake and names both the position and the length, so
/// the two bounds are asserted separately here — below 1 is a counting
/// error and past the end is a length error, and one message for both
/// would lose exactly the distinction the word exists to draw.
///
/// (In "golden" because RunSrcError redirects Console.Error, which must
/// not interleave with the other classes that do the same.)
/// </summary>
[Collection("golden")]
public class StringCodeTests
{
    // ---- CODEAT refuses where MID trims ---------------------------------

    [Fact]
    public void CodeAtPastTheEndNamesThePositionAndTheLength()
    {
        // The whole case. Mid("HELLO", 6, 1) is "" without complaint and
        // Asc("") then blames ASC; this blames the index, and says how far
        // it had to go to be wrong.
        Assert.Contains("CODEAT: position 6 is past the end of a 5-character string",
                        Fails("Print(CodeAt(\"HELLO\", 6))"));
    }

    [Fact]
    public void CodeAtAtZeroIsACountingErrorAndSaysSo()
        => Assert.Contains("CODEAT: position must be a whole number from 1",
                           Fails("Print(CodeAt(\"HELLO\", 0))"));

    [Fact]
    public void CodeAtBelowZeroIsTheSameCountingError()
        => Assert.Contains("CODEAT: position must be a whole number from 1",
                           Fails("Print(CodeAt(\"HELLO\", 0 - 1))"));

    [Fact]
    public void CodeAtOnAFractionRefusesRatherThanTruncating()
        => Assert.Contains("CODEAT: position must be a whole number from 1",
                           Fails("Print(CodeAt(\"HELLO\", 2.5))"));

    [Fact]
    public void CodeAtOnTheEmptyStringHasNoPositionToGive()
        => Assert.Contains("CODEAT: position 1 is past the end of a 0-character string",
                           Fails("Print(CodeAt(\"\", 1))"));

    // ---- FROMCODES refuses what CHR would wrap --------------------------

    [Fact]
    public void FromCodesAbove65535WouldWrapAndIsRefused()
        => Assert.Contains("FROMCODES: code 65536 is outside 0 to 65535",
                           Fails("Print(FromCodes(ToArray({ 65, 65536 })))"));

    [Fact]
    public void FromCodesBelowZeroIsRefused()
        => Assert.Contains("FROMCODES: code -1 is outside 0 to 65535",
                           Fails("Print(FromCodes(ToArray({ 0 - 1 })))"));

    [Fact]
    public void FromCodesOnAFractionRefusesBeforeTheCastTruncatesIt()
        => Assert.Contains("FROMCODES: code 1.5 is not a whole number",
                           Fails("Print(FromCodes(ToArray({ 1.5 })))"));

    [Fact]
    public void FromCodesNamesANonNumberInTheOrdinaryWay()
        => Assert.Contains("expects a NUMBER, got STRING",
                           Fails("Print(FromCodes(ToArray({ 65, \"B\" })))"));

    // ---- INSTRFROM refuses a start it cannot count from -----------------

    [Fact]
    public void InstrFromAtZeroIsRefused()
        => Assert.Contains("INSTRFROM: start must be a whole number from 1",
                           Fails("Print(InstrFrom(\"HELLO\", \"L\", 0))"));

    [Fact]
    public void InstrFromBelowZeroIsRefused()
        => Assert.Contains("INSTRFROM: start must be a whole number from 1",
                           Fails("Print(InstrFrom(\"HELLO\", \"L\", 0 - 1))"));

    [Fact]
    public void InstrFromOnAFractionIsRefused()
        => Assert.Contains("INSTRFROM: start must be a whole number from 1",
                           Fails("Print(InstrFrom(\"HELLO\", \"L\", 1.5))"));

    // ---- the boundaries that must NOT refuse ----------------------------

    [Fact]
    public void CodeAtOnTheLastCharacterIsAnOrdinaryRead()
        => Assert.Equal("79\n", Runs("Print(CodeAt(\"HELLO\", Len(\"HELLO\")))"));

    [Fact]
    public void InstrFromPastTheEndAnswersZeroRatherThanRefusing()
    {
        // .NET's IndexOf throws on a start past the end, so the guard that
        // turns this into an answer is load-bearing and not tidiness.
        // Nothing occurs there; that is an answer, not a mistake.
        Assert.Equal("0\n0\n", Runs(
            "Print(InstrFrom(\"HELLO\", \"L\", Len(\"HELLO\") + 1))",
            "Print(InstrFrom(\"\", \"L\", 1))"));
    }

    [Fact]
    public void CodesIsTotalAndTheEmptyStringAnswersTheEmptyArray()
        => Assert.Equal("0\nTrue\n", Runs(
            "Print(Length(Codes(\"\")))",
            "Print(FromCodes(Codes(\"\")) = \"\")"));

    // ---- the identities, at the level where NUL can be written ----------

    [Fact]
    public void TheRoundTripIsExactThroughANulChrCannotBuild()
    {
        // Chr(0) is "" because NUL ends a C string, so a Map over Chr
        // silently loses the character and the round trip is only nearly
        // exact. FromCodes appends a real NUL, which is the whole reason
        // it exists as a word rather than as a machine Def.
        Assert.Equal("3\nArray(72, 0, 73)\nTrue\n\n", Runs(
            "Let s = FromCodes(ToArray({ 72, 0, 73 }))",
            "Print(Len(s))",
            "Print(Codes(s))",
            "Print(Codes(s) = ToArray({ 72, 0, 73 }))",
            "Print(Chr(0))"));
    }

    [Fact]
    public void InstrFromAtOneIsInstr()
    {
        // The compatibility identity. Ordinal on both sides, without which
        // the two would disagree on some inputs under some cultures.
        Assert.Equal("True\nTrue\nTrue\n", Runs(
            "Print(InstrFrom(\"MISSISSIPPI\", \"SS\", 1) = Instr(\"MISSISSIPPI\", \"SS\"))",
            "Print(InstrFrom(\"MISSISSIPPI\", \"XY\", 1) = Instr(\"MISSISSIPPI\", \"XY\"))",
            "Print(InstrFrom(\"MISSISSIPPI\", \"\", 1) = Instr(\"MISSISSIPPI\", \"\"))"));
    }

    [Fact]
    public void FromCodesTakesTheListTheSameWayEveryOtherSequenceWordDoes()
        => Assert.Equal("AB\nAB\n", Runs(
            "Print(FromCodes(ToArray({ 65, 66 })))",
            "Print(FromCodes({ 65, 66 }))"));

    // ---- the trap this replaces, still exactly as it was ----------------

    [Fact]
    public void MidStillClampsAndAscStillAborts()
    {
        // D1: nothing existing changes, so a program that relied on either
        // still gets what it relied on. The clamp is used deliberately in
        // places — Mid(s, k, Len(s)) for "the rest" depends on it.
        Assert.Equal("0\n", Runs("Print(Len(Mid(\"HELLO\", 6, 1)))"));
        Assert.Contains("ASC of empty string", Fails("Print(Asc(Mid(\"HELLO\", 6, 1)))"));
    }

    // ---- helpers --------------------------------------------------------

    static string Body(params string[] lines) =>
        "Def Main()\n" + string.Concat(lines.Select(l => "    " + l + "\n"));

    static string Runs(params string[] lines)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(Body(lines))));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string Fails(params string[] lines)
    {
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(Body(lines))));
            int exit = Weaver.Execute(prog, null, new StringWriter(), TextReader.Null,
                                      Array.Empty<string>());
            Assert.Equal(1, exit);
            return err.ToString();
        }
        finally { Console.SetError(oldErr); }
    }

    static string WriteTemp(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-stringcode",
                                  Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(path, src);
        return path;
    }
}
