// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The Thornle pure model (mills/thornle/thornle-core.shoddy): scoring,
/// the game record, the line classifier and the keyboard legend, none of
/// which touch a terminal. The VT100 half (painting, the menu and the
/// loop, in thornle.shoddy) is driven by the mill's own scripted session.
/// Marks print as one letter each: P right place, W wrong place, - absent.
/// </summary>
public class ThornleTests
{
    static readonly string Root = RepoRoot.Dir;

    const string Ten = "    Let ten = { \"crane\", \"speed\", \"erase\", \"abbey\", \"babes\", \"eerie\", \"sense\", \"stone\", \"trace\", \"brake\" }";
    const string Fresh = "    Let fresh = Game(\"crane\", ten, { }, \"\", False, False)";

    [Fact]
    public void RepeatedLettersScoreTheWayTheOriginalDoes()
    {
        string output = Probe(
            "    Print(Row(\"crane\", \"crane\"))",
            // the second pass runs left to right over the spare copies
            "    Print(Row(\"speed\", \"erase\"))",
            // the exact hit at the end consumes the answer's only E
            "    Print(Row(\"crane\", \"eerie\"))",
            // a doubled guess letter against one spare copy
            "    Print(Row(\"abbey\", \"babes\"))");
        Assert.Equal(string.Join('\n',
            "PPPPP",
            "W--WW",
            "--W-P",
            "WWPP-",
            ""), output);
    }

    [Fact]
    public void LinesAreRefusedAcceptedAndEnded()
    {
        string output = Probe(
            Ten,
            Fresh,
            "    Let short = Submit(fresh, \"cran\")",
            "    Print(Note(short) & \" \" & Str(Length(Guesses(short))))",
            "    Let unknown = Submit(fresh, \"zzzzz\")",
            "    Print(Note(unknown) & \" \" & Str(Length(Guesses(unknown))))",
            "    Let third = Submit(Submit(Submit(fresh, \"speed\"), \"erase\"), \"crane\")",
            "    Print(Note(third) & \" won=\" & YN(Won(third)) & \" ended=\" & YN(Ended(third)))",
            "    Let lost = Fold({ \"speed\", \"erase\", \"abbey\", \"babes\", \"eerie\", \"sense\" }, fresh, Submit)",
            "    Print(Note(lost) & \" won=\" & YN(Won(lost)) & \" ended=\" & YN(Ended(lost)))",
            // once ended, a further line changes nothing
            "    Print(\"late=\" & Str(Length(Guesses(Submit(lost, \"crane\")))))");
        Assert.Equal(string.Join('\n',
            "FIVE LETTERS 0",
            "NOT IN THE LIST 0",
            "GOT IT IN 3 won=Y ended=Y",
            "IT WAS CRANE won=N ended=Y",
            "late=6",
            ""), output);
    }

    [Fact]
    public void TypedLinesClassify()
    {
        string output = Probe(
            "    Print(\"word=\" & YN(Classify(\"  CRANE \") = Guess(\"crane\")))",
            "    Print(\"q=\" & YN(Classify(\"q\") = Leave()) & \" quit=\" & YN(Classify(\"Quit\") = Leave()))",
            // any five-letter word is a guess, even one that starts with Q
            "    Print(\"quite=\" & YN(Classify(\"quite\") = Guess(\"quite\")))",
            "    Print(\"blank=\" & YN(Classify(\"   \") = Idle()))");
        Assert.Equal(string.Join('\n',
            "word=Y",
            "q=Y quit=Y",
            "quite=Y",
            "blank=Y",
            ""), output);
    }

    [Fact]
    public void TheLegendKeepsTheBestMarkForEachLetter()
    {
        string output = Probe(
            Ten,
            // speed marks E wrong-place, eerie lands it right-place, and
            // sense's absent E never overwrites the better mark
            "    Let lg = Legend(Game(\"crane\", ten, { \"speed\", \"eerie\", \"sense\" }, \"\", False, False))",
            "    Print(\"e=\" & YN(Nth(lg, 5) = Some(Placed())) & \" r=\" & YN(Nth(lg, 18) = Some(Present())))",
            "    Print(\"s=\" & YN(Nth(lg, 19) = Some(Absent())) & \" n=\" & YN(Nth(lg, 14) = Some(Present())))",
            "    Print(\"b=\" & YN(Nth(lg, 2) = None()))");
        Assert.Equal(string.Join('\n',
            "e=Y r=Y",
            "s=Y n=Y",
            "b=Y",
            ""), output);
    }

    [Fact]
    public void TheSameSeedPicksTheSameAnswer()
    {
        string output = Probe(
            Ten,
            "    Seed(7)",
            "    Let a = Answer(NewGame(ten))",
            "    Seed(7)",
            "    Let b = Answer(NewGame(ten))",
            "    Print(\"same=\" & YN(a = b) & \" listed=\" & YN(Contains(ten, a)))",
            "    Print(\"fresh=\" & YN(IsEmpty(Guesses(NewGame(ten))) And Not Ended(NewGame(ten))))");
        Assert.Equal(string.Join('\n',
            "same=Y listed=Y",
            "fresh=Y",
            ""), output);
    }

    [Fact]
    public void TheShippedListLoadsWhole()
    {
        string words = Path.Combine(Root, "mills", "thornle", "files", "words.txt").Replace('\\', '/');
        string output = Probe(
            $"    Print(Str(Length(Normalise(ReadLines(\"{words}\")))))");
        Assert.Equal("14855\n", output);
    }

    // ---- helper --------------------------------------------------------

    /// <summary>Weave and run a Main body against thornle-core.shoddy.
    /// The core's includes are bare, so the probe and the core sit in the
    /// machines directory of a temp workspace, where the includes resolve
    /// beside the file with no $SHODDYLIB in play, as PacTests does. The
    /// probe adds file.shoddy for the one test that reads the shipped
    /// list; the core itself reads no file.</summary>
    static string Probe(params string[] body)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-thornle", Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(ws, "machines");
        Directory.CreateDirectory(lib);
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(lib, Path.GetFileName(f)));
        File.Copy(Path.Combine(Root, "mills", "thornle", "thornle-core.shoddy"),
                  Path.Combine(lib, "thornle-core.shoddy"));

        string sb = Path.Combine(lib, "probe.shoddy");
        File.WriteAllText(sb,
            "Include \"thornle-core.shoddy\"\nInclude \"file.shoddy\"\n\n"
            + "Def YN(b As Boolean) As String\n    If b Then\n        \"Y\"\n    Else\n        \"N\"\n\n"
            + "Def MarkCh(m As Mark) As String\n    Select Case m\n        Case Placed\n            \"P\"\n"
            + "        Case Present\n            \"W\"\n        Case Else\n            \"-\"\n\n"
            + "Def Row(ans As String, gs As String) As String\n"
            + "    Fold(ToList(Marks(ans, gs)), \"\", Fn(a, m) => a & MarkCh(m))\n\n"
            + "Def Main()\n" + string.Join('\n', body) + "\n");

        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Parser.Parse(lines, prog);
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, machines.Machines, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }
}
